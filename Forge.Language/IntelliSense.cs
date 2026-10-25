using Forge.Common.Commands;
using Forge.Common.Editor;
using Forge.Common.Language;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Editor;
using Forge.Text;
using Forge.Workspace;

namespace Forge.Language;

public sealed class IntelliSense(IEditorHost workbench, Action refresh) : IIntelliSense
{
	public ILanguageFeatures? GetFeatures() => Features;

	public void SetFeatures(ILanguageFeatures features) => Features = features;

	public SuggestWidget Suggest { get; } = new();

	public SignatureHelpWidget Signature { get; } = new();

	public HoverWidget Hover { get; } = new();

	public OccurrenceHighlighter Occurrences => field ??= new OccurrenceHighlighter(workbench, () => Features, refresh);

	public EditorMessageWidget Message { get; } = new();

	private UiQueue Ui => field ??= new UiQueue(refresh);

	public RenameController Renaming => field ??= new RenameController(workbench, Ui, Message);

	private TypingAssist Typing => field ??= new TypingAssist(workbench, Ui);

	public NavigationController Navigation => field ??= new NavigationController(workbench, Ui, Message);

	public CodeActionController Actions => field ??= new CodeActionController(workbench, Ui, Message);

	public void ShowCodeActions() {
		if (Available && Features is { } features) Actions.Invoke(features);
	}

	public void OpenLightbulbMenu(float x, float y) {
		if (Available && Features is { } features) Actions.OpenFromLightbulb(features, x, y);
	}

	public void RevealDefinition() {
		if (Available && Features is { } features) Navigation.RevealDefinition(features);
	}

	public void GoToImplementation() {
		if (Available && Features is { } features) Navigation.GoToImplementation(features);
	}

	public void FindReferences() {
		if (Available && Features is { } features) Navigation.FindReferences(features);
	}

	public void LinkHover(int offset) {
		if (Available && Features is { } features) Navigation.Hover(features, offset);
	}

	public void StartRename() {
		if (!Available || Features is not { } features) return;

		Dismiss();
		Renaming.Start(features);
	}

	public Action<bool>? DetailsVisibleChanged;

	public ILanguageFeatures? Features { get; set; }

	private int _signatureGeneration;

	private SuggestSession Session => field ??= new SuggestSession(workbench, Suggest, Ui) { Shown = ResetResolved };

	private void ResetResolved() {
		_resolvedFor = -1;
		_resolvedEdits = [];
	}

	private long _pointerRestingSince;

	private bool _hoverRequested;

	private static bool IsWordCharacter(char character) =>
		char.IsLetterOrDigit(character) || character == '_';

	private bool Available =>
		Features is { } features && workbench.GetActiveFilePath() is { } path && features.Handles(path);

	private LanguageTriggers Triggers => Features?.GetTriggers() ?? LanguageTriggers.Default;

	public void Typed(char character) {
		if (!Available || Features is not { } features) return;

		Typing.Typed(features, character);

		if (character == '\n') {
			Session.Cancel();
			return;
		}

		LanguageTriggers triggers = Triggers;

		if (triggers.Signature.Contains(character)) RequestSignature(character);
		else if (Signature.IsOpen && triggers.SignatureRetrigger.Contains(character)) RequestSignature(character);
		else if (character == ')') Signature.Hide();

		Session.Typed(features, character);
	}

	public void TriggerSuggest() {
		if (Available && Features is { } features) Session.Invoke(features);
	}

	public void Deleted() => CursorMoved();

	public void CursorMoved() {
		if (Features is { } features) Session.CursorChanged(features);
	}

	public void Dismiss() {
		Session.Cancel();
		Signature.Hide();
		Hover.Hide();
	}

	public bool HandleKey(uint key, bool control) {
		const uint VkReturn = 0x0D;
		const uint VkEscape = 0x1B;
		const uint VkSpace = 0x20;
		const uint VkPageUp = 0x21;
		const uint VkPageDown = 0x22;
		const uint VkUp = 0x26;
		const uint VkDown = 0x28;
		const uint VkTab = 0x09;

		if (key == VkSpace && control) {
			if (Suggest.IsOpen) {
				Suggest.DetailsVisible = !Suggest.DetailsVisible;
				DetailsVisibleChanged?.Invoke(Suggest.DetailsVisible);
			}
			else TriggerSuggest();

			refresh();
			return true;
		}

		if (Suggest.IsOpen) {
			switch (key) {
				case VkEscape:
					Session.Cancel();
					refresh();
					return true;

				case VkUp:
					Suggest.MoveSelection(-1);
					refresh();
					return true;

				case VkDown:
					Suggest.MoveSelection(1);
					refresh();
					return true;

				case VkPageUp:
					Suggest.MovePage(-1);
					refresh();
					return true;

				case VkPageDown:
					Suggest.MovePage(1);
					refresh();
					return true;

				case VkReturn:
				case VkTab:
					Accept();
					return true;
			}

			return false;
		}

		if (!Signature.IsOpen) return false;

		switch (key) {
			case VkEscape:
				Signature.Hide();
				refresh();
				return true;

			case VkUp when Signature.HasOverloads:
			case VkDown when Signature.HasOverloads:
				Signature.Cycle(key == VkUp ? -1 : 1);
				refresh();
				return true;

			default:
				return false;
		}
	}

	public void Accept() {
		if (Suggest.Current is not { } entry || workbench.GetActiveDocument() is not { } document ||
			workbench.GetActiveView() is not { } view) {
			Session.Cancel();
			return;
		}

		IReadOnlyList<HostTextEdit> imports = _resolvedFor == entry.Index ? _resolvedEdits : [];
		bool resolvePending = _resolvedFor != entry.Index;

		TextSnapshot snapshot = view.GetSnapshot();
		int line = view.GetCaretLine();
		TextSpan span = snapshot.GetLineSpan(line);

		int start = Math.Clamp(span.Start + entry.ReplaceStartColumn - 1, span.Start, span.End);

		int end = Math.Clamp(view.GetCaretOffset(), start, span.End);

		document.GetBuffer().BeginEdit();
		document.GetBuffer().Replace(start, end - start, entry.InsertText);
		document.GetBuffer().EndEdit();

		view.ExternalEdit(start, entry.InsertText.Length - (end - start));

		view.MoveTo(Math.Min(start + entry.InsertText.Length, view.GetSnapshot().Length), extend: false);

		if (imports.Count > 0) ApplyEdits(document, imports, view);

		Session.Cancel();

		if (resolvePending) ApplyImportsWhenResolved(entry.Index, document, view);

		RequestSignature('\0');
		refresh();
	}

	private void ApplyImportsWhenResolved(int index, IDocument document, ITextView view) {
		if (Features is not { } features) return;

		int version = document.GetBuffer().Version;

		_ = Task.Run(async () => {
			if (await features.ResolveCompletionAsync(index).ConfigureAwait(false) is not
				{ AdditionalEdits.Count: > 0 } detail) return;

			if (document.GetBuffer().Version != version) return;

			ApplyEdits(document, detail.AdditionalEdits, view);
			refresh();
		});
	}

	private static void ApplyEdits(IDocument document, IReadOnlyList<HostTextEdit> edits, ITextView view) {
		DocumentEdits.Apply(document.GetBuffer(), edits, out int earliest, out int delta);

		if (earliest != int.MaxValue) view.ExternalEdit(earliest, delta);
	}

	private IReadOnlyList<HostTextEdit> _resolvedEdits = [];
	private int _resolvedFor = -1;

	public void Tick() {
		Ui.Drain();

		if (Message.IsOpen && workbench.GetActiveView()?.GetCaretOffset() != Message.Offset) Message.Hide();
		Occurrences.Tick();

		if (Available && Features is { } actionFeatures && Panel.Ui is { } actionUi) Actions.Tick(actionFeatures, actionUi.GetFonts().GetCode().GetCellWidth(), Panel.Scale);
		else Actions.Clear();

		if (Suggest.PendingDetail is { } index && Features is { } features) {
			int generation = Suggest.Generation;

			_ = Task.Run(async () => {
				if (await features.ResolveCompletionAsync(index).ConfigureAwait(false) is not
					{ } detail || generation != Suggest.Generation) return;

				_resolvedFor = index;
				_resolvedEdits = detail.AdditionalEdits;

				Suggest.SetDetail(index, detail.Detail, detail.Documentation);
				refresh();
			});

			Suggest.SetDetail(index, string.Empty, string.Empty);
		}

		if (_hoverRequested || _pointerRestingSince == 0 || Suggest.IsOpen ||
			Environment.TickCount64 - _pointerRestingSince < HoverWidget.DelayMilliseconds) return;

		_hoverRequested = true;

		if (!Available || Features is not { } hoverFeatures ||
			workbench.GetActiveFilePath() is not { } path) return;

		int line = Hover.Line;
		int column = Hover.Column;

		_ = Task.Run(async () => {
			if (await hoverFeatures.HoverAsync(path, line, column).ConfigureAwait(false) is { Length: > 0 } text) {
				Hover.Show(text, line, column);
				refresh();
			}
		});
	}

	public void PointerMoved(int line, int column, float x, float y) {
		if (line == Hover.Line && column == Hover.Column) return;

		Hover.Hide();
		Hover.Aim(line, column, x, y);

		_pointerRestingSince = Environment.TickCount64;
		_hoverRequested = false;
	}

	public void PointerLeft() {
		_pointerRestingSince = 0;
		_hoverRequested = false;
		Hover.Hide();
	}

	private void RequestSignature(char trigger) {
		if (Features is not { } features || workbench.GetActiveFilePath() is not { } path ||
			workbench.GetActiveView() is not { } view) return;

		workbench.PublishActiveDocument();

		int generation = ++_signatureGeneration;
		int line = view.GetCaretLine() + 1;
		int column = view.GetCaretColumn() + 1;

		_ = Task.Run(async () => {
			SignatureHelp? help = await features.SignatureHelpAsync(path, line, column, trigger)
				.ConfigureAwait(false);

			if (generation != _signatureGeneration) return;

			if (help is null) Signature.Hide();
			else Signature.Show(help);

			refresh();
		});
	}

	public IEnumerable<Panel> Widgets => [Signature, Hover, Suggest, Suggest.Details, Renaming.Widget, Message];

	public void Place(float originX, float originY, float editorLeft, float editorTop, float editorBottom, float editorRight) {
		Suggest.Accepted ??= Accept;

		if (workbench.GetActiveView() is not { } view || Panel.Ui is not { } ui) {
			Suggest.Visible = Suggest.Details.Visible = Signature.Visible = Hover.Visible = false;
			return;
		}

		ITextFont font = ui.GetFonts().GetCode();
		float lineHeight = font.GetLineHeight();

		if (Suggest.IsOpen) {
			TextSnapshot snapshot = view.GetSnapshot();
			TextSpan line = snapshot.GetLineSpan(Math.Clamp(Suggest.Line - 1, 0, Math.Max(0, snapshot.LineCount - 1)));
			int anchor = Math.Clamp(line.Start + Suggest.WordStartColumn - 1, line.Start, line.End);
			(float x, float y) = view.PointForOffset(font, anchor, editorLeft, editorTop);

			Suggest.Place(x, y, lineHeight, editorTop, editorBottom, editorRight, originX, originY);
		}
		else Suggest.Place(0f, 0f, 0f, editorTop, editorBottom, editorRight, originX, originY);

		if (Signature.IsOpen) {
			(float x, float y) = view.PointForOffset(font, view.GetCaretOffset(), editorLeft, editorTop);
			Signature.Place(x, y, lineHeight, editorTop, editorBottom, editorRight, originX, originY);
		}
		else Signature.Visible = false;

		Hover.Place(editorTop, editorBottom, editorRight, originX, originY);

		if (Renaming.Widget.IsOpen) {
			TextSnapshot snapshot = view.GetSnapshot();
			TextSpan line = snapshot.GetLineSpan(Math.Clamp(Renaming.Widget.Line - 1, 0, Math.Max(0, snapshot.LineCount - 1)));
			int anchor = Math.Clamp(line.Start + Renaming.Widget.StartColumn - 1, line.Start, line.End);
			(float x, float y) = view.PointForOffset(font, anchor, editorLeft, editorTop);

			Renaming.Widget.Place(x, y, lineHeight, editorRight, originX, originY);
		}
		else Renaming.Widget.Visible = false;

		(float caretX, float caretY) = view.PointForOffset(font, view.GetCaretOffset(), editorLeft, editorTop);
		Actions.MenuAnchor = (caretX, caretY + lineHeight);

		if (Message.IsOpen) {
			(float x, float y) = view.PointForOffset(font, Math.Min(Message.Offset, view.GetSnapshot().Length), editorLeft, editorTop);
			Message.Place(x, y, editorRight, originX, originY);
		}
		else Message.Visible = false;
	}
}
