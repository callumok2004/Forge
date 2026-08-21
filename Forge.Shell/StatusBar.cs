using Forge.Common.Render;
using Forge.Editor;
using Forge.Text;
using Forge.Theming;

namespace Forge.Shell;

public sealed class StatusBar
{
	private const int MaxSegments = 12;

	private readonly Segment[] _segments = new Segment[MaxSegments];
	private int _count;
	private int _hoverIndex = -1;

	private float _hoverScale = 1f;

	private readonly struct Segment(float x, float width, bool rightAligned, string? commandId = null)
	{
		public readonly float X = x;
		public readonly float Width = width;
		public readonly bool RightAligned = rightAligned;

		public readonly string? CommandId = commandId;
	}

	public int Errors { get; set; }

	public int Warnings { get; set; }

	public sealed record Item(string Id, Codicon Icon, string Text, string? CommandId,
		bool RightAligned = false, int Priority = 0);

	private Item[] _items = [];

	private readonly Lock _itemsSync = new();

	public IReadOnlyList<Item> Items => _items;

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
		}
	}

	public void RemoveItem(string id) {
		lock (_itemsSync) _items = [.. _items.Where(item => item.Id != id)];
	}

	public string? CommandAt(int index) =>
		index >= 0 && index < _count ? _segments[index].CommandId : null;

	public static float Height(float scale) => Metrics.StatusBarHeight * scale;

	public int IndexAt(float x, float y, float top, float scale) {
		if (y < top || y > top + Height(scale)) return -1;

		for (int i = 0; i < _count; i++) {
			if (x >= _segments[i].X && x < _segments[i].X + _segments[i].Width) return i;
		}

		return -1;
	}

	public bool SetHover(int index) {
		bool changed = _hoverIndex != index;
		_hoverIndex = index;
		return changed;
	}

	public void Render(IPainter painter, TextView? view, string? branchName, float x, float y, float width,
		float scale) {
		float height = Height(scale);
		ITextFont font = painter.GetUi();
		_hoverScale = scale;

		Item[] items = _items;

		_count = 0;

		float baseline = y + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float centreY = y + (height / 2f);

		float padding = Metrics.Space6 * scale;

		float cursor = x + padding + (Metrics.Space4 * scale);

		if (!string.IsNullOrEmpty(branchName)) {
			float start = cursor - (Metrics.SpaceSm * scale);
			painter.DrawIcon(Codicon.GitBranch, cursor + (7f * scale), centreY, 14f * scale, Theme.StatusBarForeground);
			cursor += 20f * scale;

			painter.DrawText(font, branchName, cursor, baseline, Theme.StatusBarForeground);
			cursor += font.Measure(branchName);

			AddSegment(start, cursor + (Metrics.SpaceSm * scale) - start, rightAligned: false);
			cursor += Metrics.SpaceLg * scale;
		}

		{
			float start = cursor - (Metrics.SpaceSm * scale);

			Span<char> count = stackalloc char[12];

			painter.DrawIcon(Codicon.Error, cursor + (6f * scale), centreY, 14f * scale, Theme.StatusBarForeground);
			cursor += 17f * scale;
			Errors.TryFormat(count, out int errorDigits);
			painter.DrawText(font, count[..errorDigits], cursor, baseline, Theme.StatusBarForeground);
			cursor += font.Measure(count[..errorDigits]) + (Metrics.SpaceMd * scale);

			painter.DrawIcon(Codicon.Warning, cursor + (6f * scale), centreY, 14f * scale, Theme.StatusBarForeground);
			cursor += 17f * scale;
			Warnings.TryFormat(count, out int warningDigits);
			painter.DrawText(font, count[..warningDigits], cursor, baseline, Theme.StatusBarForeground);
			cursor += font.Measure(count[..warningDigits]);

			AddSegment(start, cursor + (Metrics.SpaceSm * scale) - start, rightAligned: false,
				"workbench.panel.problems");
		}

		foreach (Item item in items) {
			if (item.RightAligned) continue;

			cursor += Metrics.SpaceLg * scale;

			float start = cursor - (Metrics.SpaceSm * scale);

			painter.DrawIcon(item.Icon, cursor + (6f * scale), centreY, 14f * scale, Theme.StatusBarForeground);
			cursor += 17f * scale;

			painter.DrawText(font, item.Text, cursor, baseline, Theme.StatusBarForeground);
			cursor += font.Measure(item.Text);

			AddSegment(start, cursor + (Metrics.SpaceSm * scale) - start, rightAligned: false,
				item.CommandId);
		}

		if (view is null) {
			DrawHover(painter, y, height);
			return;
		}

		float right = x + width - padding;

		foreach (Item item in items) {
			if (item.RightAligned) {
				right = DrawRightSegment(painter, font, item.Text, right, baseline, scale, item.CommandId);
			}
		}

		ReadOnlySpan<char> ending = view.Buffer.LineEnding == LineEnding.Lf ? "LF" : "CRLF";
		right = DrawRightSegment(painter, font, ending, right, baseline, scale);

		right = DrawRightSegment(painter, font, view.Highlighter.Language.Name, right, baseline, scale);

		Span<char> indent = stackalloc char[32];
		int indentLength = Write(indent, "Spaces: ");
		view.TabWidth.TryFormat(indent[indentLength..], out int digits);
		indentLength += digits;
		right = DrawRightSegment(painter, font, indent[..indentLength], right, baseline, scale);

		Span<char> position = stackalloc char[64];
		int positionLength = FormatPosition(position, view);
		DrawRightSegment(painter, font, position[..positionLength], right, baseline, scale);

		DrawHover(painter, y, height);
	}

	private void DrawHover(IPainter painter, float y, float height) {
		if (_hoverIndex >= 0 && _hoverIndex < _count) {
			painter.FillRoundedRectangle(_segments[_hoverIndex].X, y, _segments[_hoverIndex].Width,
				height, Metrics.RadiusSmall * _hoverScale, Theme.StatusBarHoverBackground);
		}
	}

	private float DrawRightSegment(IPainter painter, ITextFont font, ReadOnlySpan<char> text, float right,
		float baseline, float scale, string? commandId = null) {
		float textWidth = font.Measure(text);
		float left = right - textWidth;

		painter.DrawText(font, text, left, baseline, Theme.StatusBarForeground);

		AddSegment(left - (Metrics.SpaceSm * scale), textWidth + (Metrics.SpaceLg * scale),
			rightAligned: true, commandId);

		return left - (Metrics.SpaceLg * scale);
	}

	private void AddSegment(float x, float width, bool rightAligned, string? commandId = null) {
		if (_count < MaxSegments) _segments[_count++] = new Segment(x, width, rightAligned, commandId);
	}

	private static int FormatPosition(Span<char> buffer, TextView view) {
		int written = Write(buffer, "Ln ");
		(view.CaretLine + 1).TryFormat(buffer[written..], out int digits, "N0");
		written += digits;

		written += Write(buffer[written..], ", Col ");
		(view.CaretColumn + 1).TryFormat(buffer[written..], out digits, "N0");
		written += digits;

		if (view.HasSelection) {
			written += Write(buffer[written..], " (");
			view.Selection.Length.TryFormat(buffer[written..], out digits, "N0");
			written += digits;
			written += Write(buffer[written..], " selected)");
		}

		return written;
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}
}
