using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Common.Editor;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class StatusBar : Panel
{
	private const float ItemPadding = 6f;
	private const float ItemMargin = 1f;
	private const float EdgePadding = 6f;

	public sealed record Item(string Id, Codicon Icon, string Text, string? CommandId, bool RightAligned = false, int Priority = 0);

	private readonly Lock _itemsSync = new();
	private Item[] _items = [];
	private bool _itemsDirty = true;

	private readonly Button _branch;
	private readonly Button _problems;
	private readonly Button _endOfLine;
	private readonly Button _language;
	private readonly Button _indentation;
	private readonly Button _position;
	private readonly Button _resources;
	private readonly List<Button> _extensionItems = [];

	public StatusBar() {
		Name = "StatusBar";
		Dock = DockType.Bottom;

		_branch = CreateItem("git.checkout");
		_problems = CreateItem("workbench.actions.view.problems");
		_endOfLine = CreateItem("workbench.action.editor.changeEOL");
		_language = CreateItem("workbench.action.editor.changeLanguageMode");
		_indentation = CreateItem("workbench.action.editor.changeIndentation");
		_position = CreateItem("workbench.action.gotoLine");
		_resources = CreateItem("workbench.action.openProcessExplorer");
	}

	public int Errors { get; set; }

	public int Warnings { get; set; }

	public ResourceUsage? Resources { get; set; }

	public IReadOnlyList<Item> Items => _items;

	public static float Height(float scale) => Metrics.StatusBarHeight * scale;

	public void SetItem(Item item) {
		lock (_itemsSync) {
			List<Item> updated = [.. _items];
			int existing = updated.FindIndex(candidate => candidate.Id == item.Id);

			if (existing >= 0) updated[existing] = item;
			else {
				updated.Add(item);
				updated.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
			}

			_items = [.. updated];
			_itemsDirty = true;
		}
	}

	public void RemoveItem(string id) {
		lock (_itemsSync) {
			_items = [.. _items.Where(item => item.Id != id)];
			_itemsDirty = true;
		}
	}

	private static Button CreateItem(string? command) => new() {
		Command = command,
		Foreground = Theme.StatusBarForeground,
		HoverBackground = Theme.StatusBarHoverBackground,
		IconColor = IconColor,
		Padding = new Thickness(ItemPadding, 0f, ItemPadding, 0f),
		DockMargin = new Thickness(ItemMargin, 0f, ItemMargin, 0f),
		SizeToContents = true,
	};

	public void Sync(ITextView? view, string? branch) {
		if (_itemsDirty) Rebuild();

		Span<char> buffer = stackalloc char[96];

		_branch.Visible = branch is { Length: > 0 };

		if (branch is { Length: > 0 }) _branch.SetText(Format(buffer, "$(git-branch) ", branch));

		int written = Write(buffer, "$(error) ");
		Errors.TryFormat(buffer[written..], out int digits);
		written += digits;
		written += Write(buffer[written..], "  $(warning) ");
		Warnings.TryFormat(buffer[written..], out digits);
		written += digits;
		_problems.SetText(buffer[..written]);

		bool editing = view is not null;
		_endOfLine.Visible = _language.Visible = _indentation.Visible = _position.Visible = editing;

		if (view is not null) {
			_endOfLine.SetText(view.GetLineEnding() == LineEnding.Lf ? "LF" : "CRLF");
			_language.SetText(view.GetLanguageName());

			written = Write(buffer, "Spaces: ");
			view.GetTabWidth().TryFormat(buffer[written..], out digits);
			_indentation.SetText(buffer[..(written + digits)]);

			_position.SetText(buffer[..FormatPosition(buffer, view)]);
		}

		_resources.Visible = Resources is not null;

		if (Resources is { } resources) _resources.SetText(buffer[..FormatResources(buffer, resources)]);
	}

	private void Rebuild() {
		Item[] items;

		lock (_itemsSync) {
			items = _items;
			_itemsDirty = false;
		}

		foreach (Panel child in Children.ToArray()) RemoveChild(child);

		_extensionItems.Clear();

		Add(_branch, DockType.Left);
		Add(_problems, DockType.Left);

		foreach (Item item in items) {
			if (item.RightAligned) continue;

			Add(ExtensionButton(item), DockType.Left);
		}

		foreach (Item item in items) {
			if (!item.RightAligned) continue;

			Add(ExtensionButton(item), DockType.Right);
		}

		Add(_endOfLine, DockType.Right);
		Add(_language, DockType.Right);
		Add(_indentation, DockType.Right);
		Add(_position, DockType.Right);
		Add(_resources, DockType.Right);
	}

	private Button ExtensionButton(Item item) {
		Button button = CreateItem(item.CommandId);
		button.SetText(item.Icon == default ? item.Text : $"$({item.Icon}) {item.Text}");
		_extensionItems.Add(button);
		return button;
	}

	private void Add(Button button, DockType dock) {
		button.Dock = dock;
		AddChild(button);
		button.FitContents();
	}

	public override void PerformLayout() {
		float scale = Scale;
		DockPadding = new Thickness(EdgePadding * scale, 0f, EdgePadding * scale, 0f);

		foreach (Panel child in Children) {
			if (child is not Button button) continue;

			button.DockMargin = new Thickness(ItemMargin * scale, 2f * scale, ItemMargin * scale, 2f * scale);
			button.FitContents();
		}

		base.PerformLayout();
	}

	private static Color IconColor(Codicon icon) => icon switch {
		Codicon.Error => Theme.ProblemsErrorIcon,
		Codicon.Warning => Theme.ProblemsWarningIcon,
		Codicon.Info => Theme.ProblemsInfoIcon,
		Codicon.GitBranch or Codicon.SourceControl or Codicon.Sync => Theme.ActivityBarBadgeBackground,
		Codicon.Run or Codicon.Check or Codicon.DebugAlt or Codicon.DebugContinue or Codicon.DebugPause or Codicon.DebugRestart or Codicon.DebugStop or Codicon.DebugDisconnect => Theme.ChartsGreen,
		Codicon.Globe => Theme.ChartsBlue,
		_ => Theme.StatusBarForeground,
	};

	private static ReadOnlySpan<char> Format(Span<char> buffer, ReadOnlySpan<char> prefix, ReadOnlySpan<char> value) {
		int written = Write(buffer, prefix);
		int count = Math.Min(value.Length, buffer.Length - written);
		value[..count].CopyTo(buffer[written..]);
		return buffer[..(written + count)];
	}

	private static int FormatPosition(Span<char> buffer, ITextView view) {
		int written = Write(buffer, "Ln ");
		(view.GetCaretLine() + 1).TryFormat(buffer[written..], out int digits, "N0");
		written += digits;

		written += Write(buffer[written..], ", Col ");
		(view.GetCaretColumn() + 1).TryFormat(buffer[written..], out digits, "N0");
		written += digits;

		int selected = view.GetSelectionLength();

		if (selected == 0) return written;

		written += Write(buffer[written..], " (");
		selected.TryFormat(buffer[written..], out digits, "N0");
		written += digits;
		return written + Write(buffer[written..], " selected)");
	}

	private static int FormatResources(Span<char> buffer, ResourceUsage resources) {
		int written = Write(buffer, "$(pulse) ");
		resources.CpuPercent.TryFormat(buffer[written..], out int digits, "F1");
		written += digits;
		written += Write(buffer[written..], "%  $(server) ");

		double megabytes = resources.MemoryBytes / (1024.0 * 1024.0);
		bool gigabytes = megabytes >= 1024;

		(gigabytes ? megabytes / 1024 : Math.Round(megabytes)).TryFormat(buffer[written..], out digits, gigabytes ? "F2" : "F0");
		written += digits;
		return written + Write(buffer[written..], gigabytes ? " GB" : " MB");
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}
}
