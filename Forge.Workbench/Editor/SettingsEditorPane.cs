using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class SettingsEditorPane : EditorPane
{
	public const string PaneId = "workbench.editor.settings2";

	private const float MaxWidth = 1400f;
	private const float SidePadding = 24f;
	private const float HeaderTop = 14f;
	private const float SearchHeight = 30f;
	private const float TabsTop = 10f;
	private const float TabPaddingX = 8f;
	private const float TabHeight = 32f;
	private const float BodyTop = 14f;
	private const float TocWidth = 208f;
	private const float TocRowHeight = 22f;
	private const float TocIndent = 8f;
	private const float NarrowWidth = 600f;
	private const float ContentsPaddingX = 14f;
	private const float ContentsTop = 12f;
	private const float ContentsBottom = 18f;
	private const float BoolBottom = 26f;
	private const float LineHeight = 18.2f;
	private const float TitleGap = 2f;
	private const float ValueTop = 9f;
	private const float ControlHeight = 26f;
	private const float TextControlWidth = 420f;
	private const float NumberControlWidth = 200f;
	private const float EnumControlWidth = 320f;
	private const float CheckboxSize = 18f;
	private const float CheckboxGap = 9f;
	private const float GroupPadding = 10f;
	private const float GroupLeft = 15f;
	private const float GearSize = 22f;
	private const float ScrollbarWidth = 10f;
	private const float FontSize = 13f;

	private static readonly string[] CategoryOrder = ["Commonly Used", "Text Editor", "Workbench", "Window", "Features", "Application", SettingsSchema.ExtensionsCategory];

	private static readonly string[] CommonlyUsed = ["files.autoSave", "editor.fontSize", "editor.fontFamily", "editor.tabSize", "editor.renderWhitespace", "editor.cursorStyle", "editor.multiCursorModifier", "editor.insertSpaces", "editor.wordWrap", "files.exclude", "files.associations", "workbench.editor.enablePreview"];

	private enum Style : byte
	{
		Normal,
		Code,
		Link,
	}

	private readonly record struct Run(int Line, float X, string Text, Style Style, string? Target);

	private sealed class Row
	{
		public int Level;
		public string Title = string.Empty;
		public SettingDefinition? Setting;
		public float Top;
		public float Height;
		public string Category = string.Empty;
		public string Label = string.Empty;
		public readonly List<Run> Description = [];
		public int DescriptionLines;
	}

	private sealed class TocNode
	{
		public string Title = string.Empty;
		public int Level;
		public int Row;
		public int Count;
	}

	private readonly Action<string> _openJson;
	private readonly TextEntry _search;
	private readonly TextEntry _editor;
	private readonly Menu _menu = new();
	private readonly List<Row> _rows = [];
	private readonly List<TocNode> _toc = [];

	private SettingsTarget _target = SettingsTarget.User;
	private float _scrollY;
	private float _tocScrollY;
	private float _layoutWidth = -1f;
	private string _layoutQuery = string.Empty;
	private SettingsTarget _layoutTarget;
	private float _contentHeight;
	private int _hoverRow = -1;
	private int _focusRow = -1;
	private int _editingRow = -1;
	private bool _hoverGear;
	private bool _draggingThumb;
	private float _dragOffset;
	private int _resultCount;
	private bool _committing;

	public SettingsEditorPane(Action<string> openJson) {
		_openJson = openJson;
		Id = PaneId;
		Name = "SettingsEditor";
		Dock = DockType.Fill;
		KeyboardInputEnabled = true;

		_search = new TextEntry { Name = "SettingsSearch", Placeholder = "Search settings", FontSize = FontSize, TextChanged = QueryChanged };
		AddChild(_search);

		_editor = new TextEntry { Name = "SettingValue", FontSize = FontSize, TextChanged = EditorChanged, Submitted = StopEditing };
		_editor.Visible = false;
		AddChild(_editor);

		Settings.Current.Changed += SettingsChanged;
	}

	public override string Title => "Settings";

	public override Codicon Icon => Codicon.SettingsGear;

	public override void OnClosed() => Settings.Current.Changed -= SettingsChanged;

	public void FocusSearch() => _search.RequestFocus();

	private void SettingsChanged(IReadOnlyList<string> keys) {
		if (_committing) return;

		if (_editingRow >= 0 && _editingRow < _rows.Count && _rows[_editingRow].Setting is { } setting && keys.Contains(setting.Key)) _editor.SetText(DisplayText(setting, Value(setting)));

		Repaint();
	}

	#region Geometry

	private float FrameLeft => MathF.Max(0f, (Wide - (MaxWidth * Scale)) / 2f);

	private float FrameWidth => MathF.Min(Wide, MaxWidth * Scale);

	private float HeaderLeft => FrameLeft + (SidePadding * Scale);

	private float HeaderWidth => MathF.Max(0f, FrameWidth - (SidePadding * 2f * Scale));

	private float SearchTop => HeaderTop * Scale;

	private float TabsRowTop => SearchTop + (SearchHeight * Scale) + (TabsTop * Scale);

	private float BodyStart => TabsRowTop + (TabHeight * Scale) + (BodyTop * Scale);

	private float BodyHeight => MathF.Max(0f, Tall - BodyStart);

	private bool Narrow => Wide < NarrowWidth * Scale;

	private float TocLeft => HeaderLeft;

	private float TocWide => Narrow ? 0f : TocWidth * Scale;

	private float TreeLeft => Narrow ? FrameLeft : TocLeft + TocWide;

	private float TreeWidth => MathF.Max(0f, FrameLeft + FrameWidth - TreeLeft);

	private float BoxLeft => TreeLeft + (SidePadding * Scale);

	private float BoxWidth => MathF.Max(0f, TreeWidth - (SidePadding * 2f * Scale));

	private float TextLeft => BoxLeft + (ContentsPaddingX * Scale);

	private float TextWidth => MathF.Max(1f, BoxWidth - (ContentsPaddingX * 2f * Scale));

	private float MaxScroll => MathF.Max(0f, _contentHeight - BodyHeight);

	#endregion

	#region Model

	private ITextFont BodyFont => Ui!.GetFonts().GetUi(FontSize);

	private ITextFont TitleFont => Ui!.GetFonts().GetUi(FontSize, true);

	private ITextFont CodeFont => Ui!.GetFonts().GetMono(11f);

	private void QueryChanged() {
		_scrollY = 0f;
		StopEditing();
		InvalidateLayout();
		Repaint();
	}

	private void EnsureModel() {
		if (Ui is null) return;

		string query = _search.GetString().Trim();

		if (_layoutWidth == Wide && _layoutQuery == query && _layoutTarget == _target && _rows.Count > 0) return;

		_layoutWidth = Wide;
		_layoutQuery = query;
		_layoutTarget = _target;
		Build(query);
	}

	private void Build(string query) {
		_rows.Clear();
		_toc.Clear();

		string[] words = query.Length == 0 ? [] : query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		List<SettingDefinition> matches = [];

		foreach (SettingDefinition setting in SettingsSchema.All) {
			if (setting.Deprecation is not null && Value(setting, scopedOnly: true) is null) continue;

			if (words.Length == 0 || Matches(setting, words)) matches.Add(setting);
		}

		_resultCount = matches.Count;

		if (words.Length == 0) {
			List<SettingDefinition> common = [];

			foreach (string key in CommonlyUsed) {
				if (SettingsSchema.Find(key) is { } setting) common.Add(setting);
			}

			if (common.Count > 0) AddGroup("Commonly Used", null, common);
		}

		foreach (string category in CategoryOrder) {
			if (category == "Commonly Used") continue;

			List<SettingDefinition> inCategory = matches.Where(setting => setting.Category == category).ToList();

			if (inCategory.Count == 0) continue;

			IEnumerable<IGrouping<string, SettingDefinition>> sections = inCategory.GroupBy(static setting => setting.Section);

			if (category == SettingsSchema.ExtensionsCategory) sections = sections.OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase);

			AddCategory(category, sections.ToList());
		}

		Measure();
	}

	private void AddCategory(string category, List<IGrouping<string, SettingDefinition>> sections) {
		int start = _rows.Count;
		TocNode node = new() { Title = category, Level = 0, Row = start };
		_toc.Add(node);
		_rows.Add(new Row { Level = 1, Title = category });

		foreach (IGrouping<string, SettingDefinition> section in sections) {
			List<SettingDefinition> settings = [.. section.OrderBy(static setting => setting.Order).ThenBy(static setting => setting.Key, StringComparer.Ordinal)];

			if (section.Key != category && section.Key.Length > 0) {
				_toc.Add(new TocNode { Title = section.Key, Level = 1, Row = _rows.Count, Count = settings.Count });
				_rows.Add(new Row { Level = 2, Title = section.Key });
			}

			foreach (SettingDefinition setting in settings) AddSetting(setting);

			node.Count += settings.Count;
		}
	}

	private void AddGroup(string title, string? section, List<SettingDefinition> settings) {
		_toc.Add(new TocNode { Title = title, Level = 0, Row = _rows.Count, Count = settings.Count });
		_rows.Add(new Row { Level = 1, Title = section ?? title });

		foreach (SettingDefinition setting in settings) AddSetting(setting);
	}

	private void AddSetting(SettingDefinition setting) {
		(string category, string label) = DisplayParts(setting.Key);
		_rows.Add(new Row { Setting = setting, Category = category, Label = label });
	}

	private static bool Matches(SettingDefinition setting, string[] words) {
		(string category, string label) = DisplayParts(setting.Key);

		foreach (string word in words) {
			if (setting.Key.Contains(word, StringComparison.OrdinalIgnoreCase) || label.Contains(word, StringComparison.OrdinalIgnoreCase) || category.Contains(word, StringComparison.OrdinalIgnoreCase) || setting.Description.Contains(word, StringComparison.OrdinalIgnoreCase) || setting.Section.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;

			return false;
		}

		return true;
	}

	private void Measure() {
		float scale = Scale;
		float top = 0f;
		float lineHeight = LineHeight * scale;

		foreach (Row row in _rows) {
			row.Top = top;

			if (row.Setting is not { } setting) {
				ITextFont font = Ui!.GetFonts().GetUi(row.Level == 1 ? 26f : 22f, true);
				row.Height = font.GetLineHeight() + (GroupPadding * 2f * scale);
				top += row.Height;
				continue;
			}

			float width = setting.IsBoolean ? TextWidth - ((CheckboxSize + CheckboxGap) * scale) : TextWidth;
			row.Description.Clear();
			row.DescriptionLines = Wrap(Markdown(setting), width, row.Description);

			if (setting.Deprecation is { Length: > 0 } deprecation) {
				int first = row.DescriptionLines;
				List<Run> extra = [];
				int count = Wrap([(deprecation, Style.Normal, "deprecated")], width, extra);

				foreach (Run run in extra) row.Description.Add(run with { Line = run.Line + first });

				row.DescriptionLines += count;
			}

			float height = ContentsTop * scale + lineHeight + (TitleGap * scale);

			if (setting.IsBoolean) height += MathF.Max(CheckboxSize * scale, row.DescriptionLines * lineHeight) + (BoolBottom * scale);
			else height += (row.DescriptionLines * lineHeight) + (ValueTop * scale) + (setting.IsComplex ? lineHeight : ControlHeight * scale) + (ContentsBottom * scale);

			row.Height = height;
			top += height;
		}

		_contentHeight = top;
		_scrollY = Math.Clamp(_scrollY, 0f, MaxScroll);
	}

	private List<(string Text, Style Style, string? Target)> Markdown(SettingDefinition setting) {
		List<(string, Style, string?)> parts = [];
		ReadOnlySpan<char> text = setting.Description;
		StringBuilder plain = new();

		void Flush() {
			if (plain.Length == 0) return;

			parts.Add((plain.ToString(), Style.Normal, null));
			plain.Clear();
		}

		int i = 0;

		while (i < text.Length) {
			char c = text[i];

			if (c == '`') {
				int end = text[(i + 1)..].IndexOf('`');

				if (end >= 0) {
					Flush();
					string code = text.Slice(i + 1, end).ToString();

					if (code.Length > 2 && code[0] == '#' && code[^1] == '#') parts.Add((LinkTitle(code[1..^1]), Style.Link, code[1..^1]));
					else parts.Add((code, Style.Code, null));

					i += end + 2;
					continue;
				}
			}

			if (c == '#' && i + 1 < text.Length && char.IsLetter(text[i + 1])) {
				int end = text[(i + 1)..].IndexOf('#');

				if (end > 0 && !text.Slice(i + 1, end).ContainsAny(' ', '\n')) {
					Flush();
					string key = text.Slice(i + 1, end).ToString();
					parts.Add((LinkTitle(key), Style.Link, key));
					i += end + 2;
					continue;
				}
			}

			if (c == '[') {
				int close = text[i..].IndexOf("](", StringComparison.Ordinal);
				int paren = close < 0 ? -1 : text[(i + close + 2)..].IndexOf(')');

				if (close > 0 && paren >= 0) {
					Flush();
					parts.Add((text.Slice(i + 1, close - 1).ToString(), Style.Link, text.Slice(i + close + 2, paren).ToString()));
					i += close + 3 + paren;
					continue;
				}
			}

			if (c == '*' && i + 1 < text.Length && text[i + 1] == '*') {
				i += 2;
				continue;
			}

			if (c == '\r') {
				i++;
				continue;
			}

			plain.Append(c);
			i++;
		}

		Flush();
		return parts;
	}

	private static string LinkTitle(string key) {
		(string category, string label) = DisplayParts(key);
		return category.Length == 0 ? label : category + ": " + label;
	}

	private int Wrap(List<(string Text, Style Style, string? Target)> parts, float width, List<Run> runs) {
		ITextFont body = BodyFont;
		ITextFont code = CodeFont;
		float codePadding = 3f * Scale * 2f;
		int line = 0;
		float x = 0f;

		foreach ((string text, Style style, string? target) in parts) {
			ITextFont font = style == Style.Code ? code : body;
			float extra = style == Style.Code ? codePadding : 0f;
			int start = 0;

			while (start < text.Length) {
				if (text[start] == '\n') {
					line++;
					x = 0f;
					start++;

					if (start < text.Length && text[start] == '\n') {
						line++;
						start++;
					}

					continue;
				}

				int end = start;

				while (end < text.Length && text[end] != ' ' && text[end] != '\n') end++;

				if (end < text.Length && text[end] == ' ') end++;

				string word = text[start..end];
				float measured = font.Measure(word) + extra;

				if (x > 0f && x + measured > width && style != Style.Code) {
					line++;
					x = 0f;
					word = word.TrimStart();
				}

				if (word.Length > 0) {
					if (runs.Count > 0 && runs[^1] is { } previous && previous.Line == line && previous.Style == style && previous.Target == target && style != Style.Code) runs[^1] = previous with { Text = previous.Text + word };
					else runs.Add(new Run(line, x, word, style, target));
				}

				x += font.Measure(word) + extra;
				start = end;
			}
		}

		return runs.Count == 0 ? 0 : line + 1;
	}

	private static (string Category, string Label) DisplayParts(string key) {
		int dot = key.LastIndexOf('.');

		if (dot < 0) return (string.Empty, Wordify(key));

		string[] segments = key[..dot].Split('.');

		for (int i = 0; i < segments.Length; i++) segments[i] = Wordify(segments[i]);

		return (string.Join(" › ", segments), Wordify(key[(dot + 1)..]));
	}

	private static string Wordify(string segment) {
		StringBuilder builder = new(segment.Length + 8);

		for (int i = 0; i < segment.Length; i++) {
			char c = segment[i];

			if (c is '-' or '_') {
				builder.Append(' ');
				continue;
			}

			if (i > 0 && char.IsUpper(c) && (char.IsLower(segment[i - 1]) || (i + 1 < segment.Length && char.IsLower(segment[i + 1]) && char.IsUpper(segment[i - 1])))) builder.Append(' ');

			builder.Append(builder.Length == 0 || builder[^1] == ' ' ? char.ToUpperInvariant(c) : c);
		}

		return builder.ToString();
	}

	#endregion

	#region Values

	private JsonNode? Value(SettingDefinition setting, bool scopedOnly = false) {
		Settings settings = Settings.Current;
		JsonNode? scoped = _target == SettingsTarget.Workspace ? settings.WorkspaceValue(setting.Key) : settings.UserValue(setting.Key);

		if (scoped is not null || scopedOnly) return scoped;

		if (_target == SettingsTarget.Workspace && settings.UserValue(setting.Key) is { } user) return user;

		return setting.Default;
	}

	private bool IsModified(SettingDefinition setting) => Value(setting, scopedOnly: true) is not null;

	private string? OtherScopeNote(SettingDefinition setting) {
		Settings settings = Settings.Current;

		if (_target == SettingsTarget.User && settings.HasWorkspace && settings.WorkspaceValue(setting.Key) is not null) return "Also modified in: Workspace";

		if (_target == SettingsTarget.Workspace && settings.UserValue(setting.Key) is not null) return "Also modified in: User";

		return null;
	}

	private static string DisplayText(SettingDefinition setting, JsonNode? value) => value switch {
		null => string.Empty,
		JsonValue scalar when scalar.TryGetValue(out string? text) => text,
		JsonValue scalar when scalar.TryGetValue(out double number) => number.ToString(CultureInfo.InvariantCulture),
		_ => value.ToJsonString(),
	};

	private void Commit(SettingDefinition setting, JsonNode? value) {
		if (_target == SettingsTarget.User && value is not null && JsonNode.DeepEquals(value, setting.Default)) value = null;

		_committing = true;

		try {
			Settings.Current.SetValue(setting.Key, value, _target);
		}
		finally {
			_committing = false;
		}

		Repaint();
	}

	private void EditorChanged() {
		if (_editingRow < 0 || _editingRow >= _rows.Count || _rows[_editingRow].Setting is not { } setting) return;

		ReadOnlySpan<char> text = _editor.Text;

		if (setting.IsNumber) {
			bool valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && (!setting.IsInteger || number == Math.Floor(number)) && (setting.Minimum is not { } minimum || number >= minimum) && (setting.Maximum is not { } maximum || number <= maximum);
			_editor.Invalid = !valid;

			if (valid) Commit(setting, setting.IsInteger ? JsonValue.Create((long)number) : JsonValue.Create(number));

			return;
		}

		Commit(setting, JsonValue.Create(text.ToString()));
	}

	private void StopEditing() {
		if (_editingRow < 0) return;

		_editingRow = -1;
		_editor.Visible = false;
		_editor.Invalid = false;
		Repaint();
	}

	private void StartEditing(int index) {
		if (_rows[index].Setting is not { } setting) return;

		_editingRow = index;
		_editor.SetText(DisplayText(setting, Value(setting)));
		_editor.Visible = true;
		PositionEditor();
		_editor.RequestFocus();
		_editor.SelectAll();
		Repaint();
	}

	private (float X, float Y, float W, float H) ControlRect(Row row) {
		float scale = Scale;
		SettingDefinition setting = row.Setting!;
		float top = BodyStart + row.Top - _scrollY + (ContentsTop * scale) + (LineHeight * scale) + (TitleGap * scale);

		if (setting.IsBoolean) return (TextLeft, top + (2f * scale), CheckboxSize * scale, CheckboxSize * scale);

		top += (row.DescriptionLines * LineHeight * scale) + (ValueTop * scale);

		float width = setting.IsEnum ? EnumControlWidth : setting.IsNumber ? NumberControlWidth : TextControlWidth;

		if (setting.IsComplex) return (TextLeft, top, BodyFont.Measure("Edit in settings.json"), LineHeight * scale);

		return (TextLeft, top, MathF.Min(width * scale, TextWidth), ControlHeight * scale);
	}

	private void PositionEditor() {
		if (_editingRow < 0 || _editingRow >= _rows.Count) return;

		(float x, float y, float w, float h) = ControlRect(_rows[_editingRow]);
		_editor.SetBounds(x, y, w, h);
		_editor.Visible = y + h > BodyStart && y < Tall;
	}

	#endregion

	#region Layout

	public override void PerformLayout() {
		if (Ui is null) return;

		float scale = Scale;
		_search.SetBounds(HeaderLeft, SearchTop, HeaderWidth, SearchHeight * scale);
		_search.TextInset = new Thickness(0f, 0f, 140f, 0f);
		EnsureModel();
		PositionEditor();
	}

	#endregion

	#region Input

	private int RowAt(float y) {
		float content = y - BodyStart + _scrollY;

		if (y < BodyStart) return -1;

		int low = 0;
		int high = _rows.Count - 1;

		while (low <= high) {
			int mid = (low + high) >>> 1;
			Row row = _rows[mid];

			if (content < row.Top) high = mid - 1;
			else if (content >= row.Top + row.Height) low = mid + 1;
			else return mid;
		}

		return -1;
	}

	private bool InTree(float x) => x >= TreeLeft && x < TreeLeft + TreeWidth - (ScrollbarWidth * Scale);

	private bool InScrollbar(float x, float y) => _contentHeight > BodyHeight && y >= BodyStart && x >= Wide - (ScrollbarWidth * Scale);

	private (float Top, float Height) Thumb {
		get {
			float body = BodyHeight;
			float height = MathF.Max(20f * Scale, body * body / MathF.Max(1f, _contentHeight));
			float top = MaxScroll <= 0f ? 0f : _scrollY / MaxScroll * (body - height);
			return (BodyStart + top, height);
		}
	}

	private bool OverGear(Row row, float x, float y) {
		float scale = Scale;
		float left = BoxLeft - (GearSize * scale);
		float top = BodyStart + row.Top - _scrollY + (8f * scale);
		return x >= left && x < left + (GearSize * scale) && y >= top && y < top + (GearSize * scale);
	}

	public override void OnCursorMoved(float x, float y) {
		if (_draggingThumb) {
			(_, float height) = Thumb;
			float track = MathF.Max(1f, BodyHeight - height);
			SetScroll((y - BodyStart - _dragOffset) / track * MaxScroll);
			return;
		}

		int hover = InTree(x) || x >= BoxLeft - (GearSize * Scale) ? RowAt(y) : -1;

		if (hover >= 0 && _rows[hover].Setting is null) hover = -1;

		bool gear = hover >= 0 && OverGear(_rows[hover], x, y);

		if (hover != _hoverRow || gear != _hoverGear) {
			_hoverRow = hover;
			_hoverGear = gear;
			Repaint();
		}

		Cursor = hover >= 0 && (gear || OverControl(_rows[hover], x, y)) ? CursorCode.Hand : y < BodyStart && TabAt(x, y) is not null ? CursorCode.Hand : x < TreeLeft && TocAt(x, y) >= 0 ? CursorCode.Hand : CursorCode.Arrow;
	}

	private bool OverControl(Row row, float x, float y) {
		if (row.Setting is not { } setting) return false;

		(float cx, float cy, float cw, float ch) = ControlRect(row);

		if (setting.IsBoolean) {
			float descriptionRight = TextLeft + TextWidth;
			return x >= cx && x < descriptionRight && y >= cy && y < cy + MathF.Max(ch, row.DescriptionLines * LineHeight * Scale);
		}

		return x >= cx && x < cx + cw && y >= cy && y < cy + ch;
	}

	public override void OnCursorExited() {
		if (_hoverRow < 0) return;

		_hoverRow = -1;
		_hoverGear = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (InScrollbar(x, y)) {
			(float top, float height) = Thumb;

			if (y < top || y >= top + height) SetScroll(_scrollY + (y < top ? -BodyHeight : BodyHeight));

			(top, _) = Thumb;
			_draggingThumb = true;
			_dragOffset = y - top;
			Ui?.SetMouseCapture(this);
			return true;
		}

		if (y < BodyStart) {
			if (TabAt(x, y) is { } tab) {
				_target = tab;
				StopEditing();
				InvalidateLayout();
				Repaint();
			}
			else if (JsonButtonHit(x, y)) {
				OpenJson();
			}

			return true;
		}

		if (x < TreeLeft) {
			int toc = TocAt(x, y);

			if (toc >= 0) SetScroll(_rows[_toc[toc].Row].Top);

			return true;
		}

		int index = RowAt(y);

		if (index < 0 || _rows[index].Setting is not { } setting) {
			StopEditing();
			return true;
		}

		_focusRow = index;
		Row row = _rows[index];

		if (OverGear(row, x, y) || button == MouseButton.Right) {
			OpenGearMenu(setting, x, y);
			return true;
		}

		if (row.Description.Count > 0 && LinkAt(row, x, y) is { } link) {
			FollowLink(link);
			return true;
		}

		if (!OverControl(row, x, y)) {
			StopEditing();
			Repaint();
			return true;
		}

		if (setting.IsBoolean) {
			StopEditing();
			bool current = Value(setting) is JsonValue value && value.TryGetValue(out bool flag) && flag;
			Commit(setting, JsonValue.Create(!current));
		}
		else if (setting.IsEnum) {
			StopEditing();
			OpenEnumMenu(row, setting);
		}
		else if (setting.IsComplex) {
			OpenJson();
		}
		else {
			StartEditing(index);
		}

		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_draggingThumb) return false;

		_draggingThumb = false;

		if (ReferenceEquals(Ui?.GetMouseCapture(), this)) Ui!.SetMouseCapture(null);

		Repaint();
		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		if (x < TreeLeft && y >= BodyStart) {
			float maximum = MathF.Max(0f, (_toc.Count * TocRowHeight * Scale) - BodyHeight);
			_tocScrollY = Math.Clamp(_tocScrollY - (delta / 120f * 3f * TocRowHeight * Scale), 0f, maximum);
			Repaint();
			return true;
		}

		SetScroll(_scrollY - (delta / 120f * 3f * LineHeight * 2f * Scale));
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		const uint KeyF = 0x46;
		const uint Escape = 0x1B;

		if (key == KeyF && (modifiers & KeyModifiers.Control) != 0) {
			_search.RequestFocus();
			_search.SelectAll();
			return true;
		}

		if (key == Escape && _search.Text.Length > 0) {
			_search.Clear();
			QueryChanged();
			return true;
		}

		return false;
	}

	private void SetScroll(float value) {
		float clamped = Math.Clamp(value, 0f, MaxScroll);

		if (clamped == _scrollY) return;

		_scrollY = clamped;
		PositionEditor();
		Repaint();
	}

	private SettingsTarget? TabAt(float x, float y) {
		if (y < TabsRowTop || y >= TabsRowTop + (TabHeight * Scale)) return null;

		float left = HeaderLeft;

		foreach ((SettingsTarget target, string label) in Tabs()) {
			float width = TabWidth(label);

			if (x >= left && x < left + width) return target;

			left += width;
		}

		return null;
	}

	private IEnumerable<(SettingsTarget Target, string Label)> Tabs() {
		yield return (SettingsTarget.User, "User");

		if (Settings.Current.HasWorkspace) yield return (SettingsTarget.Workspace, "Workspace");
	}

	private float TabWidth(string label) => BodyFont.Measure(label) + (TabPaddingX * 2f * Scale);

	private (float X, float Y, float Size) JsonButton {
		get {
			float size = 22f * Scale;
			return (HeaderLeft + HeaderWidth - size, TabsRowTop + (((TabHeight * Scale) - size) / 2f), size);
		}
	}

	private bool JsonButtonHit(float x, float y) {
		(float bx, float by, float size) = JsonButton;
		return x >= bx && x < bx + size && y >= by && y < by + size;
	}

	private int TocAt(float x, float y) {
		if (Narrow || y < BodyStart || x < TocLeft || x >= TocLeft + TocWide) return -1;

		int index = (int)((y - BodyStart + _tocScrollY) / (TocRowHeight * Scale));
		return index >= 0 && index < _toc.Count ? index : -1;
	}

	private string? LinkAt(Row row, float x, float y) {
		float scale = Scale;
		float lineHeight = LineHeight * scale;
		float top = BodyStart + row.Top - _scrollY + (ContentsTop * scale) + lineHeight + (TitleGap * scale);
		float left = row.Setting!.IsBoolean ? TextLeft + ((CheckboxSize + CheckboxGap) * scale) : TextLeft;

		foreach (Run run in row.Description) {
			if (run.Style != Style.Link) continue;

			float runTop = top + (run.Line * lineHeight);
			float runLeft = left + run.X;

			if (x >= runLeft && x < runLeft + BodyFont.Measure(run.Text) && y >= runTop && y < runTop + lineHeight) return run.Target;
		}

		return null;
	}

	private void FollowLink(string target) {
		if (target.Contains("://", StringComparison.Ordinal)) {
			try {
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
			}
			catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) {
			}

			return;
		}

		_search.SetText(target);
		QueryChanged();
	}

	private void OpenJson() {
		string? path = _target == SettingsTarget.Workspace ? Settings.Current.WorkspaceSettingsPath : Settings.Current.UserPath;

		if (path is not null) _openJson(path);
	}

	private void OpenGearMenu(SettingDefinition setting, float x, float y) {
		(float left, float top) = GetAbsPos();
		_menu.Clear();
		_menu.Add(new MenuItem("Reset Setting", action: () => Commit(setting, null)) { Enabled = IsModified(setting) });
		_menu.AddSeparator();
		_menu.Add(new MenuItem("Copy Setting ID", action: () => Forge.Core.Native.Clipboard.SetText(setting.Key)));
		_menu.Add(new MenuItem("Copy Setting as JSON", action: () => Forge.Core.Native.Clipboard.SetText($"\"{setting.Key}\": {(Value(setting) ?? JsonValue.Create((string?)null))?.ToJsonString() ?? "null"}")));
		_menu.Open(left + x, top + y);
	}

	private void OpenEnumMenu(Row row, SettingDefinition setting) {
		(float left, float top) = GetAbsPos();
		(float cx, float cy, _, float ch) = ControlRect(row);
		JsonNode? current = Value(setting);
		_menu.Clear();

		for (int i = 0; i < setting.Enum!.Length; i++) {
			string option = setting.Enum[i];
			JsonNode? node = EnumNode(setting, option);
			_menu.Add(new MenuItem(EnumLabel(option), action: () => Commit(setting, node?.DeepClone())) { Checked = JsonNode.DeepEquals(node, current) });
		}

		_menu.Open(left + cx, top + cy + ch + (2f * Scale));
	}

	private static JsonNode? EnumNode(SettingDefinition setting, string option) {
		if (setting.Types is ["string"] || setting.Types.Length == 0) return JsonValue.Create(option);

		try {
			return JsonNode.Parse(option);
		}
		catch (JsonException) {
			return JsonValue.Create(option);
		}
	}

	private static string EnumLabel(string option) => option switch {
		"\n" => "\\n",
		"\r\n" => "\\r\\n",
		_ => option,
	};

	#endregion

	#region Painting

	public override void Paint(IPainter painter) {
		EnsureModel();

		float scale = Scale;
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);
		PaintHeader(painter, scale);

		painter.PushClip(ScreenX, ScreenY + BodyStart, Wide, BodyHeight);
		PaintToc(painter, scale);
		PaintRows(painter, scale);
		PaintScrollbar(painter, scale);
		painter.PopClip();
	}

	private void PaintHeader(IPainter painter, float scale) {
		ITextFont font = BodyFont;
		string count = _layoutQuery.Length == 0 ? string.Empty : _resultCount == 1 ? "1 Setting Found" : $"{_resultCount} Settings Found";

		if (count.Length > 0) {
			float countX = ScreenX + HeaderLeft + HeaderWidth - font.Measure(count) - (8f * scale);
			float baseline = ScreenY + SearchTop + (((SearchHeight * scale) - font.GetLineHeight()) / 2f) + font.GetBaseline();
			painter.DrawText(font, count, countX, baseline, Theme.DescriptionForeground);
		}

		float left = ScreenX + HeaderLeft;
		float rowTop = ScreenY + TabsRowTop;
		float rowHeight = TabHeight * scale;

		foreach ((SettingsTarget target, string label) in Tabs()) {
			float width = TabWidth(label);
			bool active = target == _target;
			float baseline = rowTop + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			painter.DrawText(font, label, left + (TabPaddingX * scale), baseline, active ? Theme.SettingsHeaderForeground : Theme.Foreground.WithAlpha(0.9f));

			if (active) painter.FillRectangle(left, rowTop + rowHeight - scale, width, scale, Theme.SettingsHeaderForeground);

			left += width;
		}

		painter.FillRectangle(ScreenX + HeaderLeft, rowTop + rowHeight, HeaderWidth, scale, Theme.PanelBorder);

		(float bx, float by, float size) = JsonButton;
		painter.DrawIcon(Codicon.GoToFile, ScreenX + bx + (size / 2f), ScreenY + by + (size / 2f), 16f * scale, Theme.Foreground);
	}

	private int CurrentToc() {
		int current = -1;

		for (int i = 0; i < _toc.Count; i++) {
			if (_rows[_toc[i].Row].Top <= _scrollY + 1f) current = i;
		}

		return Math.Max(0, current);
	}

	private void PaintToc(IPainter painter, float scale) {
		if (Narrow || _toc.Count == 0) return;

		int selected = CurrentToc();
		float rowHeight = TocRowHeight * scale;
		float top = ScreenY + BodyStart - _tocScrollY;
		bool searching = _layoutQuery.Length > 0;

		for (int i = 0; i < _toc.Count; i++) {
			float y = top + (i * rowHeight);

			if (y + rowHeight < ScreenY + BodyStart || y > ScreenY + Tall) continue;

			TocNode node = _toc[i];
			ITextFont font = Ui!.GetFonts().GetUi(FontSize, i == selected);
			float x = ScreenX + TocLeft + (TocIndent * scale) + (node.Level * TocIndent * 2f * scale);
			float baseline = y + ((rowHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			string title = searching ? $"{node.Title} ({node.Count})" : node.Title;

			painter.PushClip(ScreenX + TocLeft, y, TocWide - (8f * scale), rowHeight);
			painter.DrawText(font, title, x, baseline, i == selected ? Theme.SettingsHeaderForeground : Theme.Foreground.WithAlpha(0.9f));
			painter.PopClip();
		}
	}

	private void PaintRows(IPainter painter, float scale) {
		if (_rows.Count == 0) {
			if (_layoutQuery.Length > 0) {
				ITextFont font = BodyFont;
				painter.DrawText(font, "No Settings Found", ScreenX + BoxLeft, ScreenY + BodyStart + (20f * scale) + font.GetBaseline(), Theme.Foreground);
			}

			return;
		}

		int first = Math.Max(0, RowAt(BodyStart));
		float bottom = _scrollY + BodyHeight;

		painter.PushClip(ScreenX + TreeLeft, ScreenY + BodyStart, TreeWidth, BodyHeight);

		for (int i = first; i < _rows.Count; i++) {
			Row row = _rows[i];

			if (row.Top > bottom) break;

			float y = ScreenY + BodyStart + row.Top - _scrollY;

			if (!painter.NeedsPaint(ScreenX + TreeLeft, y, TreeWidth, row.Height)) continue;

			if (row.Setting is null) PaintGroup(painter, row, y, scale);
			else PaintSetting(painter, i, row, y, scale);
		}

		painter.PopClip();
	}

	private void PaintGroup(IPainter painter, Row row, float y, float scale) {
		ITextFont font = Ui!.GetFonts().GetUi(row.Level == 1 ? 26f : 22f, true);
		float baseline = y + (GroupPadding * scale) + font.GetBaseline();
		painter.DrawText(font, row.Title, ScreenX + BoxLeft + (GroupLeft * scale), baseline, Theme.SettingsHeaderForeground);
	}

	private void PaintSetting(IPainter painter, int index, Row row, float y, float scale) {
		SettingDefinition setting = row.Setting!;
		float boxLeft = ScreenX + BoxLeft;
		float boxWidth = BoxWidth;
		float lineHeight = LineHeight * scale;
		bool focused = index == _focusRow;
		bool hovered = index == _hoverRow;

		if (focused) {
			painter.FillRectangle(boxLeft, y, boxWidth, row.Height, Theme.SettingsFocusedRowBackground);

			if (HasFocusWithin()) painter.StrokeRoundedRectangle(boxLeft + (scale / 2f), y + (scale / 2f), boxWidth - scale, row.Height - scale, 0f, scale, Theme.FocusBorder);
		}
		else if (hovered) {
			painter.FillRectangle(boxLeft, y, boxWidth, row.Height, Theme.SettingsRowHoverBackground);
		}

		if (IsModified(setting)) {
			float bottomInset = (setting.IsBoolean ? 23f : 18f) * scale;
			painter.FillRectangle(boxLeft + (5f * scale), y + (15f * scale), 2f * scale, MathF.Max(0f, row.Height - (15f * scale) - bottomInset), Theme.SettingsModifiedItemIndicator);
		}

		if (hovered || focused) {
			float gear = GearSize * scale;
			painter.DrawIcon(Codicon.Gear, boxLeft - gear + (gear / 2f), y + (8f * scale) + (gear / 2f), 16f * scale, _hoverGear && hovered ? Theme.Foreground : Theme.Foreground.WithAlpha(0.8f));
		}

		ITextFont title = TitleFont;
		ITextFont body = BodyFont;
		float textLeft = ScreenX + TextLeft;
		float titleTop = y + (ContentsTop * scale);
		float titleBaseline = titleTop + ((lineHeight - title.GetLineHeight()) / 2f) + title.GetBaseline();
		float x = textLeft;

		painter.PushClip(textLeft, titleTop, TextWidth, lineHeight);

		if (row.Category.Length > 0) {
			string category = row.Category + ": ";
			painter.DrawText(title, category, x, titleBaseline, Theme.SettingsHeaderForeground.WithAlpha(0.9f));
			x += title.Measure(category);
		}

		painter.DrawText(title, row.Label, x, titleBaseline, Theme.SettingsHeaderForeground);
		x += title.Measure(row.Label) + (7f * scale);

		if (OtherScopeNote(setting) is { } note) painter.DrawText(body, note, x, titleBaseline, Theme.Foreground.WithAlpha(0.9f));

		painter.PopClip();

		float descriptionTop = titleTop + lineHeight + (TitleGap * scale);
		float descriptionLeft = setting.IsBoolean ? textLeft + ((CheckboxSize + CheckboxGap) * scale) : textLeft;

		PaintDescription(painter, row, descriptionLeft, descriptionTop, lineHeight, scale);

		if (setting.IsBoolean) {
			PaintCheckbox(painter, setting, textLeft, descriptionTop + (2f * scale), scale);
			return;
		}

		(float cx, float cy, float cw, float ch) = ControlRect(row);
		cx += ScreenX;
		cy += ScreenY;

		if (setting.IsComplex) {
			float baseline = cy + ((lineHeight - body.GetLineHeight()) / 2f) + body.GetBaseline();
			painter.DrawText(body, "Edit in settings.json", cx, baseline, Theme.TextLinkForeground);
			return;
		}

		if (index == _editingRow && _editor.Visible) return;

		float radius = Metrics.RadiusSmall * scale;
		string text = DisplayText(setting, Value(setting));
		float textBaseline = cy + ((ch - body.GetLineHeight()) / 2f) + body.GetBaseline();

		if (setting.IsEnum) {
			painter.FillRoundedRectangle(cx, cy, cw, ch, radius, Theme.DropdownBackground);
			painter.StrokeRoundedRectangle(cx, cy, cw, ch, radius, scale, Theme.DropdownBorder);
			painter.PushClip(cx, cy, cw - (23f * scale), ch);
			painter.DrawText(body, EnumLabel(text), cx + (6f * scale), textBaseline, Theme.DropdownForeground);
			painter.PopClip();
			painter.DrawIcon(Codicon.ChevronDown, cx + cw - (4f * scale) - (8f * scale), cy + (ch / 2f), 16f * scale, Theme.DropdownForeground);
			return;
		}

		painter.FillRoundedRectangle(cx, cy, cw, ch, radius, Theme.InputBackground);
		painter.StrokeRoundedRectangle(cx, cy, cw, ch, radius, scale, Theme.InputBorder);
		painter.PushClip(cx + (6f * scale), cy, cw - (12f * scale), ch);
		painter.DrawText(body, text, cx + (6f * scale), textBaseline, Theme.InputForeground);
		painter.PopClip();
	}

	private bool HasFocusWithin() {
		Panel? focus = Ui?.GetFocus();

		while (focus is not null) {
			if (ReferenceEquals(focus, this)) return true;

			focus = focus.Parent;
		}

		return false;
	}

	private void PaintDescription(IPainter painter, Row row, float left, float top, float lineHeight, float scale) {
		ITextFont body = BodyFont;
		ITextFont code = CodeFont;
		float bodyOffset = ((lineHeight - body.GetLineHeight()) / 2f) + body.GetBaseline();
		float codeOffset = ((lineHeight - code.GetLineHeight()) / 2f) + code.GetBaseline();
		float codePad = 3f * scale;

		foreach (Run run in row.Description) {
			float x = left + run.X;
			float y = top + (run.Line * lineHeight);

			switch (run.Style) {
				case Style.Code: {
					float width = code.Measure(run.Text) + (codePad * 2f);
					float height = 15f * scale;
					painter.FillRoundedRectangle(x, y + ((lineHeight - height) / 2f), width, height, 4f * scale, Theme.TextPreformatBackground);
					painter.DrawText(code, run.Text, x + codePad, y + codeOffset, Theme.TextPreformatForeground);
					break;
				}
				case Style.Link:
					painter.DrawText(body, run.Text, x, y + bodyOffset, Theme.TextLinkForeground);
					break;
				default:
					painter.DrawText(body, run.Text, x, y + bodyOffset, run.Target == "deprecated" ? Theme.ErrorForeground : Theme.Foreground.WithAlpha(0.9f));
					break;
			}
		}
	}

	private void PaintCheckbox(IPainter painter, SettingDefinition setting, float x, float y, float scale) {
		float size = CheckboxSize * scale;
		bool value = Value(setting) is JsonValue node && node.TryGetValue(out bool flag) && flag;

		painter.FillRoundedRectangle(x, y, size, size, 3f * scale, Theme.CheckboxBackground);
		painter.StrokeRoundedRectangle(x, y, size, size, 3f * scale, scale, Theme.CheckboxBorder);

		if (value) painter.DrawIcon(Codicon.Check, x + (size / 2f), y + (size / 2f), 16f * scale, Theme.CheckboxForeground);
	}

	private void PaintScrollbar(IPainter painter, float scale) {
		if (_contentHeight <= BodyHeight || !(IsCursorOver || _draggingThumb || HasHoverWithin())) return;

		(float top, float height) = Thumb;
		float width = ScrollbarWidth * scale;
		Color color = _draggingThumb ? Theme.ScrollbarSliderActive : Theme.ScrollbarSlider;
		painter.FillRectangle(ScreenX + Wide - width, ScreenY + top, width, height, color);
	}

	private bool HasHoverWithin() {
		Panel? over = Ui?.GetMouseOver();

		while (over is not null) {
			if (ReferenceEquals(over, this)) return true;

			over = over.Parent;
		}

		return false;
	}

	#endregion
}
