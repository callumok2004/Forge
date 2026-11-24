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

	private readonly List<ILanguageFeatures> _providers = [];

	public ILanguageFeatures? FeaturesFor(string path) {
		foreach (ILanguageFeatures provider in _providers)
			if (provider.Handles(path)) return provider;

		return null;
	}

	public void AddFeatures(ILanguageFeatures features) {
		if (!_providers.Contains(features)) _providers.Add(features);
	}

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

	public CodeLensController CodeLenses => field ??= new CodeLensController(workbench, Ui);

	public Common.Debug.IDebug? Debug { get; set; }

	public DebugValuesController DebugValues => field ??= new DebugValuesController(workbench, Ui) { Hover = { Changed = refresh } };

	private float _editorLeft;
	private float _editorTop;
	private float _editorWidth;

	public void ExecuteCodeLens(int index) {
		if (Features is { } features) CodeLenses.Execute(features, index);
	}

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

	public ILanguageFeatures? Features {
		get {
			if (_providers.Count == 0) return null;

			if (_providers.Count > 1 && workbench.GetActiveFilePath() is { } path) {
				foreach (ILanguageFeatures provider in _providers) {
					if (provider.Handles(path)) return provider;
				}
			}

			return _providers[0];
		}
		set {
			if (value is null || _providers.Contains(value)) return;

			_providers.Insert(0, value);
		}
	}

	private int _signatureGeneration;

	private SuggestSession Session => field ??= new SuggestSession(workbench, Suggest, Ui) { Shown = ResetResolved };

	private void ResetResolved() {
		_resolvedFor = -1;
		_resolvedEdits = [];
	}

	private long _pointerRestingSince;

	private bool _hoverRequested;

	private (int Line, int Column, float X, float Y)? _pendingPointer;

	private bool _pendingHide;

	private long _pendingSince;

	private float _pointerX;

	private int _hoverStart = -1;

	private int _hoverEnd = -1;

	private bool _hoverWired;

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
		_hoverStart = _hoverEnd = -1;
		_pendingHide = false;
		_pendingPointer = null;
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

		if (entry.ReplacesToEnd && end < span.End) end++;

		string insert = entry.InsertText;
		int cursor = insert.IndexOf("$0", StringComparison.Ordinal);

		if (cursor >= 0) insert = insert.Remove(cursor, 2);
		else cursor = insert.Length;

		document.GetBuffer().BeginEdit();
		document.GetBuffer().Replace(start, end - start, insert);
		document.GetBuffer().EndEdit();

		view.ExternalEdit(start);

		view.MoveTo(Math.Min(start + cursor, view.GetSnapshot().Length), extend: false);

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

		if (earliest != int.MaxValue) view.ExternalEdit(earliest);
	}

	private IReadOnlyList<HostTextEdit> _resolvedEdits = [];
	private int _resolvedFor = -1;

	private TextView? _hinted;
	private IReadOnlyDictionary<int, LineHint>? _languageHints;
	private IReadOnlyDictionary<int, LineHint>? _extensionHints;
	private IReadOnlyDictionary<int, LineHint>? _mergedHints;

	private void SyncLineHints() {
		TextView? view = workbench.GetActiveView() as TextView;
		string? path = workbench.GetActiveFilePath();
		IReadOnlyDictionary<int, LineHint>? language = view is not null && path is not null && Features is { } features && features.Handles(path) ? features.LineHints(path, view.Buffer.Version) : null;
		IReadOnlyDictionary<int, LineHint>? extension = view is not null && path is not null ? workbench.ExtensionLineHints(path) : null;
		IReadOnlyDictionary<int, LineHint>? hints = MergeHints(language, extension);

		if (!ReferenceEquals(_hinted, view) && _hinted is not null) {
			_hinted.LineHints = null;
			_hinted = null;
		}

		if (view is null || ReferenceEquals(view.LineHints, hints)) return;

		view.LineHints = hints;
		_hinted = view;
		refresh();
	}

	private IReadOnlyDictionary<int, LineHint>? MergeHints(IReadOnlyDictionary<int, LineHint>? language, IReadOnlyDictionary<int, LineHint>? extension) {
		if (extension is null) return language;

		if (language is null) return extension;

		if (ReferenceEquals(language, _languageHints) && ReferenceEquals(extension, _extensionHints)) return _mergedHints;

		Dictionary<int, LineHint> merged = new(language);

		foreach ((int line, LineHint hint) in extension) merged.TryAdd(line, hint);

		_languageHints = language;
		_extensionHints = extension;
		_mergedHints = merged;
		return merged;
	}

	public void Tick() {
		Ui.Drain();

		if (Message.IsOpen && workbench.GetActiveView()?.GetCaretOffset() != Message.Offset) Message.Hide();
		Occurrences.Tick();
		SyncLineHints();

		if (Panel.Ui is { } lensUi) {
			IFontSet fonts = lensUi.GetFonts();
			CodeLenses.Tick(Features, Core.Settings.Current.GetBoolean("editor.codeLens") ?? true, fonts.GetCodeLens().GetLineHeight(), fonts.GetCode().GetLineHeight());
			DebugValues.Tick(Debug, Features, InlineValuesEnabled(), _editorWidth, fonts.GetCode().GetCellWidth());
		}

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

		WireHover();

		if (Hover.AdvanceQuickFixPlaceholder()) refresh();

		if (_pendingHide && Environment.TickCount64 - _pendingSince >= HoverWidget.HidingDelayMilliseconds) {
			_pendingHide = false;

			if (!PointerOverHover && !Hover.HasFocus) {
				(int Line, int Column, float X, float Y)? pending = _pendingPointer;
				_pendingPointer = null;
				Hover.Hide();
				_hoverStart = _hoverEnd = -1;

				if (pending is { } target) Aim(target.Line, target.Column, target.X, target.Y);
				else _pointerRestingSince = 0;

				refresh();
			}
			else _pendingPointer = null;
		}

		if (_hoverRequested || _pointerRestingSince == 0 || Suggest.IsOpen ||
			Environment.TickCount64 - _pointerRestingSince < HoverWidget.DelayMilliseconds) return;

		_hoverRequested = true;

		if (!Available || workbench.GetActiveFilePath() is not { } path) return;

		int line = Hover.Line;
		int column = Hover.Column;

		if (Debug is { } debug && workbench.GetActiveView() is { } debugView && Panel.Ui is { } debugUi && (debugUi.GetModifiers() & KeyModifiers.Alt) == 0) {
			ITextFont code = debugUi.GetFonts().GetCode();
			TextSnapshot snapshot = debugView.GetSnapshot();

			if (line >= 1 && line <= snapshot.LineCount) {
				int lineStart = snapshot.GetLineStartOffset(line - 1);
				(float anchorX, float lineTop) = debugView.PointForOffset(code, lineStart, _editorLeft, _editorTop);

				if (DebugValues.TryHover(debug, Features, path, debugView, line, column, anchorX, lineTop, code.GetLineHeight(), offset => debugView.PointForOffset(code, lineStart + offset, _editorLeft, _editorTop).X, () => RequestLanguageHover(path, line, column))) return;
			}
		}

		ShowMarkerHover(path, line, column);
		RequestLanguageHover(path, line, column);
	}

	private bool PointerOverHover => Panel.Ui?.GetMouseOver() is { } over && (over == Hover || over.HasParent(Hover));

	private void WireHover() {
		if (_hoverWired) return;

		_hoverWired = true;
		Hover.Changed = refresh;
		Hover.LinkRequested = static href => {
			try {
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(href) { UseShellExecute = true })?.Dispose();
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) {
			}
		};
		Hover.ViewProblemRequested = marker => {
			if (workbench.GetActiveView() is not TextView view) return;

			int offset = view.Snapshot.GetLineStartOffset(marker.Line) + marker.StartColumn;
			view.MoveTo(Math.Min(offset, view.Snapshot.Length), extend: false);
			_hoverStart = _hoverEnd = -1;
			workbench.RestoreEditorFocus();
			refresh();
		};
		Hover.QuickFixRequested = (marker, x, y) => {
			if (workbench.GetActiveView() is not TextView view || Features is not { } features) return;

			int offset = view.Snapshot.GetLineStartOffset(marker.Line) + marker.StartColumn;
			view.MoveTo(Math.Min(offset, view.Snapshot.Length), extend: false);
			_hoverStart = _hoverEnd = -1;
			workbench.RestoreEditorFocus();
			Actions.OpenFromLightbulb(features, x, y);
			refresh();
		};
	}

	private void ShowMarkerHover(string path, int line, int column) {
		if (workbench.GetActiveView() is not TextView view || Panel.Ui is not { } ui) return;

		TextSnapshot snapshot = view.Snapshot;
		int index = line - 1;

		if (index < 0 || index >= snapshot.LineCount) return;

		ITextFont code = ui.GetFonts().GetCode();
		int lineStart = snapshot.GetLineStartOffset(index);
		int lineLength = snapshot.GetLineLength(index);
		int character = Math.Clamp(column - 1, 0, lineLength);
		float endX = view.PointForOffset(code, lineStart + lineLength, _editorLeft, _editorTop).X;

		if (_pointerX > endX + (code.GetCellWidth() / 2f)) return;

		int start = character;
		int end = character;

		while (start > 0 && IsWordCharacter(snapshot[lineStart + start - 1])) start--;

		while (end < lineLength && IsWordCharacter(snapshot[lineStart + end])) end++;

		List<HoverWidget.Marker> markers = [];

		foreach (TextView.Squiggle squiggle in view.Squiggles ?? []) {
			if (squiggle.Line > index || squiggle.EndLine < index) continue;

			int markerStart = squiggle.Line == index ? squiggle.StartColumn : 0;
			int markerEnd = squiggle.EndLine == index ? squiggle.EndColumn : lineLength;

			if (markerStart > character + 1 || character > markerEnd) continue;

			markers.Add(new HoverWidget.Marker(index, markerStart, markerEnd, squiggle.Message, squiggle.Severity, squiggle.Code, squiggle.Href));
			start = Math.Min(start, markerStart);
			end = Math.Max(end, markerEnd);
		}

		_hoverStart = start;
		_hoverEnd = end;
		Hover.ShowMarkers(markers, line, column);

		if (markers.Count == 0 || Features is not { } features) return;

		HoverWidget.Marker first = markers.MinBy(static marker => marker.Severity);

		_ = Task.Run(async () => {
			IReadOnlyList<CodeActionEntry> actions = await features.CodeActionsAsync(path, line, first.StartColumn + 1, line, first.EndColumn + 1, automatic: false).ConfigureAwait(false);
			Hover.SetQuickFixes(actions.Any(static action => action.DisabledReason is null && action.Kind.StartsWith("quickfix", StringComparison.Ordinal)), line, column);
			refresh();
		});
	}

	private void RequestLanguageHover(string path, int line, int column) {
		if (Features is not { } hoverFeatures || line != Hover.Line || column != Hover.Column) return;

		_ = Task.Run(async () => {
			if (await hoverFeatures.HoverAsync(path, line, column).ConfigureAwait(false) is { Length: > 0 } text) {
				Hover.Show(text, line, column);
				refresh();
			}
		});
	}

	public void PointerMoved(int line, int column, float x, float y) {
		if (line == Hover.Line && column == Hover.Column && !_pendingHide) return;

		if (DebugValues.Hover.IsOpen && (line != DebugValues.Hover.Line || column - 1 < DebugValues.Hover.StartColumn || column - 1 > DebugValues.Hover.EndColumn)) DebugValues.CancelHover();

		if (Hover.IsOpen) {
			if (Hover.HasFocus || Hover.IsSelecting) return;

			if (line == Hover.Line && column - 1 >= _hoverStart && column - 1 <= _hoverEnd) {
				_pendingHide = false;
				_pendingPointer = null;
				return;
			}

			_pendingPointer = (line, column, x, y);

			if (!_pendingHide) {
				_pendingHide = true;
				_pendingSince = Environment.TickCount64;
			}

			return;
		}

		Aim(line, column, x, y);
	}

	private void Aim(int line, int column, float x, float y) {
		Hover.Hide();
		Hover.Aim(line, column, x, y);
		_pointerX = x;
		_hoverStart = _hoverEnd = -1;
		_pointerRestingSince = Environment.TickCount64;
		_hoverRequested = false;
	}

	public void PointerLeft() {
		_pointerRestingSince = 0;
		_hoverRequested = false;

		if (Hover.IsOpen && (PointerOverHover || Hover.HasFocus)) {
			_pendingHide = false;
			_pendingPointer = null;
		}
		else {
			Hover.Hide();
			_hoverStart = _hoverEnd = -1;
		}

		if (Panel.Ui?.GetMouseOver() is { } over && (over == DebugValues.Hover || over.Parent == DebugValues.Hover)) return;

		DebugValues.CancelHover();
	}

	private static bool InlineValuesEnabled() => Core.Settings.Current.GetString("debug.inlineValues") switch {
		"off" or "false" => false,
		_ => Core.Settings.Current.GetBoolean("debug.inlineValues") ?? true,
	};

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

	public IEnumerable<Panel> Widgets => [Signature, Hover, DebugValues.Hover, Suggest, Suggest.Details, Renaming.Widget, Message];

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

		if (Hover.IsOpen && _hoverStart >= 0) {
			TextSnapshot hoverSnapshot = view.GetSnapshot();
			int hoverLine = Math.Clamp(Hover.Line - 1, 0, Math.Max(0, hoverSnapshot.LineCount - 1));
			int hoverLineStart = hoverSnapshot.GetLineStartOffset(hoverLine);
			int hoverLength = hoverSnapshot.GetLineLength(hoverLine);
			(float primaryX, float lineTop) = view.PointForOffset(font, hoverLineStart + Math.Clamp(Hover.Column - 1, 0, hoverLength), editorLeft, editorTop);
			float secondaryX = view.PointForOffset(font, hoverLineStart + Math.Clamp(_hoverStart, 0, hoverLength), editorLeft, editorTop).X;

			Hover.Anchor(primaryX, secondaryX, lineTop, lineHeight);
		}

		Hover.Place(0f, ui.GetViewportHeight(), editorRight, originX, originY, editorRight - editorLeft);

		_editorLeft = editorLeft;
		_editorTop = editorTop;
		_editorWidth = editorRight - editorLeft;

		DebugHoverWidget debugHover = DebugValues.Hover;
		debugHover.Place(editorTop, editorBottom, editorRight, originX, originY);

		if (view is TextView textView) {
			TextView.HoverRange? range = debugHover.IsOpen ? new TextView.HoverRange(debugHover.Line - 1, debugHover.StartColumn, debugHover.EndColumn) : Hover.IsOpen && _hoverStart >= 0 && _hoverEnd > _hoverStart ? new TextView.HoverRange(Hover.Line - 1, _hoverStart, _hoverEnd) : null;

			if (textView.HoverHighlight != range) textView.HoverHighlight = range;
		}

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
