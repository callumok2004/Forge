using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Editor;
using Forge.Text;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class FindWidget : Panel
{
	public const float Width = 419f;
	public const float Offset = 0f;
	public const float RightMargin = 28f;

	private const float FindHeight = 34f;
	private const float ReplaceHeight = 64f;
	private const float PaddingLeft = 9f;
	private const float ToggleWidth = 18f;
	private const float PartLeft = 17f;
	private const float PartTop = 3f;
	private const float PartGap = 4f;
	private const float InputWidth = 221f;
	private const float InputHeight = 25f;
	private const float ButtonSize = 22f;
	private const float ButtonGap = 3f;
	private const float CountWidth = 69f;
	private const float CloseTop = 5f;
	private const float CloseRight = 4f;
	private const float Radius = 6f;
	private const float OptionSize = 20f;
	private const float OptionGap = 1f;
	private const float OptionInset = 2f;

	private readonly FindModel _model = new();
	private readonly TextEntry _find;
	private readonly TextEntry _replace;
	private readonly InputToggle _matchCase;
	private readonly InputToggle _wholeWord;
	private readonly InputToggle _regex;
	private readonly InputToggle _preserveCase;
	private readonly ActionButton _toggleReplace;
	private readonly ActionButton _previous;
	private readonly ActionButton _next;
	private readonly ActionButton _inSelection;
	private readonly ActionButton _close;
	private readonly ActionButton _replaceOne;
	private readonly ActionButton _replaceAll;

	private TextView? _attached;
	private int _current = -1;

	public FindWidget() {
		Name = "FindWidget";
		Visible = false;

		_find = new TextEntry { Name = "FindInput", Placeholder = "Find", FontSize = 13f, ContextKey = ContextKeys.FindInputFocused, TextChanged = QueryChanged };
		_replace = new TextEntry { Name = "ReplaceInput", Placeholder = "Replace", FontSize = 13f, ContextKey = ContextKeys.ReplaceInputFocused };
		_matchCase = Option(Codicon.CaseSensitive, "Match Case (Alt+C)");
		_wholeWord = Option(Codicon.WholeWord, "Match Whole Word (Alt+W)");
		_regex = Option(Codicon.Regex, "Use Regular Expression (Alt+R)");
		_preserveCase = new InputToggle(Codicon.PreserveCase, "Preserve Case (Alt+P)");

		_toggleReplace = Button(Codicon.ChevronRight, "Toggle Replace", () => SetReplaceVisible(!IsReplaceOpen));
		_previous = Button(Codicon.ArrowUp, "Previous Match (Shift+Enter)", () => Reveal(Step(View?.Invoke(), -1)));
		_next = Button(Codicon.ArrowDown, "Next Match (Enter)", () => Reveal(Step(View?.Invoke(), 1)));
		_inSelection = Button(Codicon.FindSelection, "Find in Selection (Alt+L)", ToggleFindInSelection);
		_close = Button(Codicon.Close, "Close (Escape)", Hide);
		_replaceOne = Button(Codicon.Replace, "Replace (Enter)", ReplaceOne);
		_replaceAll = Button(Codicon.ReplaceAll, "Replace All (Ctrl+Alt+Enter)", ReplaceAll);

		foreach (Panel child in (Panel[])[_find, _replace, _matchCase, _wholeWord, _regex, _preserveCase, _toggleReplace, _previous, _next, _inSelection, _close, _replaceOne, _replaceAll]) AddChild(child);
	}

	private InputToggle Option(Codicon icon, string title) => new(icon, title) { Changed = _ => OptionsChanged() };

	private static ActionButton Button(Codicon icon, string name, Action action) => new() { Name = name, Icon = icon, Foreground = Theme.EditorForeground, HoverBackground = Theme.ToolbarHoverBackground, CheckedBackground = Theme.InputOptionActiveBackground, CheckedForeground = Theme.InputOptionActiveForeground, Radius = 5f, Action = action };

	public Func<TextView?>? View { get; set; }

	public Action<bool>? Revealed { get; set; }

	public bool IsOpen => Visible;

	public bool IsReplaceOpen { get; private set; }

	public bool MatchCase => _matchCase.Checked;

	public ReadOnlySpan<char> Query => _find.Text;

	private FindOptions Options => new(_matchCase.Checked, _wholeWord.Checked, _regex.Checked);

	private (int Start, int End)? _scope;

	public void Show(TextView? view) => Open(view, replace: false);

	public void ShowReplace(TextView? view) => Open(view, replace: true);

	private void Open(TextView? view, bool replace) {
		Visible = true;
		if (replace) SetReplaceVisible(true);

		if (view is not null && Seed(view) is { Length: > 0 } seed) _find.SetText(seed);

		if (replace && !_find.Text.IsEmpty) _replace.RequestFocus();
		else _find.RequestFocus();

		(replace && !_find.Text.IsEmpty ? _replace : _find).SelectAll();
		Sync(view);
		Parent?.InvalidateLayout();
		Repaint();
	}

	private static string? Seed(TextView view) {
		if (view.HasSelection) {
			string selected = view.GetSelectedText();
			return selected.Contains('\n') ? null : selected;
		}

		TextSnapshot snapshot = view.Snapshot;
		int caret = view.CaretOffset;
		int start = caret;
		int end = caret;

		while (start > 0 && IsWordCharacter(snapshot[start - 1])) start--;

		while (end < snapshot.Length && IsWordCharacter(snapshot[end])) end++;

		return end > start ? snapshot.GetText(start, end - start) : null;
	}

	private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

	public void Hide() {
		Visible = false;
		_scope = null;
		_inSelection.Checked = false;
		Detach();
		Revealed?.Invoke(false);
	}

	private void Detach() {
		if (_attached is null) return;

		_attached.FindMatches = null;
		_attached.CurrentFindMatch = -1;
		_attached.FindScope = null;
		_attached = null;
	}

	public void SetReplaceVisible(bool visible) {
		IsReplaceOpen = visible;
		_toggleReplace.Icon = visible ? Codicon.ChevronDown : Codicon.ChevronRight;
		InvalidateLayout();
		Parent?.InvalidateLayout();
		Repaint();
	}

	public void ToggleReplace(TextView? view) {
		if (!Visible) {
			ShowReplace(view);
			return;
		}

		SetReplaceVisible(!IsReplaceOpen);

		if (IsReplaceOpen) _replace.RequestFocus();
	}

	public void ToggleMatchCase() => Flip(_matchCase);

	public void ToggleWholeWord() => Flip(_wholeWord);

	public void ToggleRegex() => Flip(_regex);

	public void TogglePreserveCase() {
		_preserveCase.Checked = !_preserveCase.Checked;
		Repaint();
	}

	private void Flip(InputToggle toggle) {
		toggle.Checked = !toggle.Checked;
		OptionsChanged();
	}

	public void ToggleFindInSelection() {
		TextView? view = View?.Invoke();

		if (_scope is not null || view is null || !view.HasSelection) {
			_scope = null;
			_inSelection.Checked = false;
		}
		else {
			TextSpan selection = view.Selection;
			_scope = (selection.Start, selection.End);
			_inSelection.Checked = true;
		}

		OptionsChanged();
	}

	private void OptionsChanged() => Reveal(Restart(View?.Invoke()));

	private void QueryChanged() => Reveal(Restart(View?.Invoke()));

	private void Reveal(bool found) {
		Repaint();
		Revealed?.Invoke(found);
	}

	private void Sync(TextView? view) {
		if (!ReferenceEquals(view, _attached)) Detach();

		if (view is null || !Visible) return;

		_attached = view;
		_model.Update(view.Snapshot, _find.GetString(), Options, _scope);
		view.FindMatches = _model.Matches;
		view.FindScope = _scope;

		TextSpan selection = view.Selection;
		_current = view.HasSelection ? _model.IndexOf(selection.Start, selection.Length) : -1;
		view.CurrentFindMatch = _current;
	}

	public bool Restart(TextView? view) {
		Sync(view);

		if (view is null || _model.Matches.Count == 0) return false;

		int start = _scope is { } scope && (view.Selection.Start < scope.Start || view.Selection.Start > scope.End) ? scope.Start : view.Selection.Start;
		Select(view, _model.Next(start));
		return true;
	}

	public bool Step(TextView? view, int direction) {
		Sync(view);

		if (view is null || _model.Matches.Count == 0) return false;

		TextSpan selection = view.Selection;
		int index = direction > 0 ? _model.Next(_current >= 0 ? selection.Start + 1 : view.CaretOffset) : _model.Previous(_current >= 0 ? selection.Start : view.CaretOffset);
		Select(view, index);
		return true;
	}

	private void Select(TextView view, int index) {
		if (index < 0 || index >= _model.Matches.Count) return;

		FindMatch match = _model.Matches[index];
		view.MoveTo(match.Start, extend: false);
		view.MoveTo(match.Start + match.Length, extend: true);
		_current = index;
		view.CurrentFindMatch = index;
	}

	public void ReplaceOne() {
		if (View?.Invoke() is not { } view) return;

		Sync(view);

		if (_current < 0) {
			Reveal(Step(view, 1));
			return;
		}

		FindMatch match = _model.Matches[_current];
		string text = view.Snapshot.GetText();
		string replacement = _model.ReplacementFor(text, match, _replace.GetString(), _preserveCase.Checked);

		view.PushUndoStop();
		view.Buffer.Replace(match.Start, match.Length, replacement);
		view.ExternalEdit(match.Start, replacement.Length - match.Length);
		view.MoveTo(match.Start + replacement.Length, extend: false);
		view.PushUndoStop();

		if (_scope is { } scope) _scope = (scope.Start, scope.End + replacement.Length - match.Length);

		Sync(view);

		if (_model.Matches.Count > 0) Select(view, _model.Next(match.Start + replacement.Length));

		Reveal(_model.Matches.Count > 0);
	}

	public void ReplaceAll() {
		if (View?.Invoke() is not { } view) return;

		Sync(view);

		if (_model.Matches.Count == 0) return;

		string text = view.Snapshot.GetText();
		string replacement = _replace.GetString();
		FindMatch[] matches = [.. _model.Matches];

		view.PushUndoStop();
		view.Buffer.BeginEdit();

		int delta = 0;

		for (int i = matches.Length - 1; i >= 0; i--) {
			FindMatch match = matches[i];
			string value = _model.ReplacementFor(text, match, replacement, _preserveCase.Checked);
			view.Buffer.Replace(match.Start, match.Length, value);
			delta += value.Length - match.Length;
		}

		view.Buffer.EndEdit();
		view.ExternalEdit(matches[0].Start, delta);
		view.PushUndoStop();

		if (_scope is { } scope) _scope = (scope.Start, scope.End + delta);

		Sync(view);
		Reveal(false);
	}

	private string CountText() {
		if (_find.Text.IsEmpty) return "No results";

		if (_model.InvalidPattern || _model.Matches.Count == 0) return "No results";

		string total = _model.Matches.Count >= FindModel.MatchesLimit ? $"{FindModel.MatchesLimit}+" : _model.Matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return _current >= 0 ? $"{_current + 1} of {total}" : $"? of {total}";
	}

	public float PreferredHeight => (IsReplaceOpen ? ReplaceHeight : FindHeight) * Scale;

	public override void PerformLayout() {
		float scale = Scale;
		float height = PreferredHeight;
		SetSize(Width * scale, height);

		float input = InputHeight * scale;
		float button = ButtonSize * scale;
		float left = (PaddingLeft + PartLeft) * scale;
		float findTop = PartTop * scale;
		float inputWidth = InputWidth * scale;

		_toggleReplace.SetBounds(0f, 0f, ToggleWidth * scale, height);

		_find.SetBounds(left, findTop, inputWidth, input);
		_find.TextInset = new Thickness(0f, 0f, (OptionSize + OptionGap) * 3f + OptionInset, 0f);

		float option = OptionSize * scale;
		float optionTop = findTop + ((input - option) / 2f);
		float optionRight = left + inputWidth - (OptionInset * scale);

		_regex.SetBounds(optionRight - option, optionTop, option, option);
		_wholeWord.SetBounds(optionRight - (option * 2f) - (OptionGap * scale), optionTop, option, option);
		_matchCase.SetBounds(optionRight - (option * 3f) - (OptionGap * 2f * scale), optionTop, option, option);

		float x = left + inputWidth + (ButtonGap * scale) + (CountWidth * scale);
		float buttonTop = findTop + ((input - button) / 2f);

		_previous.SetBounds(x + (ButtonGap * scale), buttonTop, button, button);
		x += button + (ButtonGap * scale);
		_next.SetBounds(x + (ButtonGap * scale), buttonTop, button, button);
		x += button + (ButtonGap * scale);
		_inSelection.SetBounds(x + (ButtonGap * scale), buttonTop, button, button);

		_close.SetBounds(Wide - (CloseRight * scale) - button, CloseTop * scale, button, button);

		float replaceTop = findTop + input + (PartGap * scale);
		_replace.Visible = IsReplaceOpen;
		_preserveCase.Visible = IsReplaceOpen;
		_replaceOne.Visible = IsReplaceOpen;
		_replaceAll.Visible = IsReplaceOpen;

		_replace.SetBounds(left, replaceTop, inputWidth, input);
		_replace.TextInset = new Thickness(0f, 0f, OptionSize + OptionInset, 0f);
		_preserveCase.SetBounds(optionRight - option, replaceTop + ((input - option) / 2f), option, option);

		float replaceButtons = left + inputWidth + (ButtonGap * scale);
		_replaceOne.SetBounds(replaceButtons, replaceTop + ((input - button) / 2f), button, button);
		_replaceAll.SetBounds(replaceButtons + button + (ButtonGap * scale), replaceTop + ((input - button) / 2f), button, button);
	}

	public override void PaintBackground(IPainter painter) {
		Sync(View?.Invoke());

		float scale = Scale;
		float radius = Radius * scale;
		painter.FillRoundedRectangle(ScreenX + (scale * 2f), ScreenY + (scale * 3f), Wide, Tall, radius, Theme.WidgetShadow);
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.EditorWidgetBackground);
		painter.StrokeRoundedRectangle(ScreenX + (scale / 2f), ScreenY + (scale / 2f), Wide - scale, Tall - scale, radius, scale, Theme.WidgetBorder);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi(12f);
		string count = CountText();
		bool none = !_find.Text.IsEmpty && (_model.Matches.Count == 0 || _model.InvalidPattern);
		float left = ScreenX + _find.X + _find.Wide + (ButtonGap * scale);
		float width = CountWidth * scale;
		float baseline = ScreenY + _find.Y + ((_find.Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();

		painter.PushClip(left, ScreenY + _find.Y, width, _find.Tall);
		painter.DrawText(font, count, left + (2f * scale), baseline, none ? Theme.ErrorForeground : Theme.EditorWidgetForeground);
		painter.PopClip();
	}
}
