using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Editor;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class FindWidget : Panel
{
	public const float Width = 340f;
	public const float Offset = 8f;

	private const float Height = 32f;
	private const float FieldHeight = 22f;
	private const float ButtonSize = 20f;
	private const float PaddingX = 8f;
	private const float CountWidth = 72f;

	private readonly TextEntry _field;
	private readonly Label _count;
	private readonly ActionButton _matchCase;

	private int _matchCount;
	private int _matchRank;
	private int _countedVersion = -1;
	private string _countedQuery = string.Empty;

	public FindWidget() {
		Name = "FindWidget";
		Visible = false;
		MouseInputEnabled = false;

		_field = new TextEntry { Name = "FindInput", Placeholder = "Find", Dock = DockType.Fill, ContextKey = ContextKeys.FindInputFocused, TextInset = new Thickness(0f, 0f, CountWidth, 0f), TextChanged = QueryChanged };
		_count = new Label { Name = "FindCount", Foreground = Theme.SectionHeaderForeground, Alignment = TextAlignment.Right };
		_matchCase = Button(Codicon.CaseSensitive, "Match Case", ToggleMatchCase);

		AddChild(Button(Codicon.Close, "Close", Hide));
		AddChild(Button(Codicon.ChevronDown, "Next Match", () => Reveal(Step(View?.Invoke(), 1))));
		AddChild(Button(Codicon.ChevronUp, "Previous Match", () => Reveal(Step(View?.Invoke(), -1))));
		AddChild(_matchCase);
		AddChild(_field);
		AddChild(_count);
	}

	private static ActionButton Button(Codicon icon, string name, Action action) => new() { Name = name, Icon = icon, Dock = DockType.Right, Foreground = Theme.EditorForeground, HoverBackground = Theme.ListHoverBackground, CheckedBackground = Theme.ListActiveSelectionBackground, CheckedForeground = Theme.EditorForeground, Action = action };

	public Func<TextView?>? View { get; set; }

	public Action<bool>? Revealed { get; set; }

	public bool IsOpen => Visible;

	public bool MatchCase { get; private set; }

	public ReadOnlySpan<char> Query => _field.Text;

	public void Show(TextView? view) {
		Visible = true;

		if (view is { HasSelection: true } selected && selected.GetSelectedText() is var text && !text.Contains('\n')) _field.SetText(text);

		_field.RequestFocus();
		_field.SelectAll();
	}

	public void Hide() {
		Visible = false;
		Revealed?.Invoke(false);
	}

	public void ToggleMatchCase() {
		MatchCase = !MatchCase;
		_matchCase.Checked = MatchCase;
		Invalidate();
		Reveal(Restart(View?.Invoke()));
	}

	private void QueryChanged() {
		Invalidate();
		Reveal(Restart(View?.Invoke()));
	}

	private void Reveal(bool found) {
		Repaint();
		Revealed?.Invoke(found);
	}

	private void Invalidate() => _countedVersion = -1;

	public bool Restart(TextView? view) {
		if (view is null || _field.Text.IsEmpty) return false;

		string query = _field.GetString();
		int hit = TextSearch.Next(view.Snapshot, query, view.Selection.Start, MatchCase);

		if (hit < 0) return false;

		view.MoveTo(hit, extend: false);
		view.MoveTo(hit + query.Length, extend: true);
		return true;
	}

	public bool Step(TextView? view, int direction) {
		if (view is null || _field.Text.IsEmpty) return false;

		string query = _field.GetString();
		int from = direction > 0 ? Math.Min(view.Selection.Start + 1, view.Snapshot.Length) : view.Selection.Start;
		int hit = direction > 0 ? TextSearch.Next(view.Snapshot, query, from, MatchCase) : TextSearch.Previous(view.Snapshot, query, from, MatchCase);

		if (hit < 0) return false;

		view.MoveTo(hit, extend: false);
		view.MoveTo(hit + query.Length, extend: true);
		return true;
	}

	private void Recount(TextView? view) {
		if (view is null || _field.Text.IsEmpty) {
			_matchCount = _matchRank = 0;
			return;
		}

		string query = _field.GetString();

		if (_countedVersion != view.Snapshot.Version || _countedQuery != query) {
			_countedVersion = view.Snapshot.Version;
			_countedQuery = query;
			_matchCount = TextSearch.Count(view.Snapshot, query, MatchCase);
		}

		_matchRank = _matchCount == 0 ? 0 : TextSearch.RankOf(view.Snapshot, query, view.Selection.Start, MatchCase);
	}

	private void SyncCount() {
		Recount(View?.Invoke());

		bool searching = !_field.Text.IsEmpty;
		_field.Invalid = searching && _matchCount == 0;

		if (!searching) {
			_count.SetText(default);
			return;
		}

		if (_matchCount == 0) {
			_count.SetText("No results");
			return;
		}

		Span<char> buffer = stackalloc char[32];
		_matchRank.TryFormat(buffer, out int written);
		" of ".CopyTo(buffer[written..]);
		written += 4;
		_matchCount.TryFormat(buffer[written..], out int digits);
		_count.SetText(buffer[..(written + digits)]);
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = Height * scale;
		float field = FieldHeight * scale;
		float button = ButtonSize * scale;
		float vertical = (height - field) / 2f;

		SetSize(Width * scale, height);
		DockPadding = new Thickness(PaddingX * scale, vertical, PaddingX * scale, vertical);

		foreach (Panel child in Children) {
			if (child is not ActionButton action) continue;

			action.SetSize(button, button);
			action.DockMargin = new Thickness(Metrics.Space2 * scale, (field - button) / 2f, 0f, (field - button) / 2f);
		}

		_matchCase.DockMargin = new Thickness(Metrics.Space8 * scale, (field - button) / 2f, 0f, (field - button) / 2f);

		base.PerformLayout();

		_count.SetBounds(_field.X + _field.Wide - (CountWidth * scale), _field.Y, (CountWidth - Metrics.Space4) * scale, _field.Tall);
	}

	public override void PaintBackground(IPainter painter) {
		SyncCount();

		float scale = Scale;
		float radius = Metrics.RadiusMedium * scale;
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.WidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}
}
