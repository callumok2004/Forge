using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public enum ErrorListScope
{
	EntireSolution,
	OpenDocuments,
	CurrentProject,
	CurrentDocument,
}

public enum ErrorListSource
{
	BuildAndIntelliSense,
	BuildOnly,
	IntelliSenseOnly,
}

public sealed class ProblemsView : Panel
{
	private const float ToolbarHeight = 30f;
	private const float HeaderHeight = 22f;
	private const float RowHeight = 20f;
	private const float CellPadding = 6f;
	private const float ToolbarPadding = 6f;
	private const float ToolbarGap = 4f;
	private const float ControlHeight = 22f;
	private const float ScopeWidth = 140f;
	private const float SourceWidth = 160f;
	private const float SearchWidth = 280f;
	private const float SearchMinWidth = 140f;
	private const float SeparatorHeight = 16f;
	private const float SeparatorMargin = 2f;

	private static readonly string[] ScopeLabels = ["Entire Solution", "Open Documents", "Current Project", "Current Document"];
	private static readonly string[] SourceLabels = ["Build + IntelliSense", "Build Only", "IntelliSense Only"];

	private static readonly Column[] Columns = [
		new(string.Empty, 0f, 36f, 36f),
		new("Code", 1f, 70f, 160f),
		new("Description", 6f, 0f, float.MaxValue),
		new("Project", 1.5f, 80f, float.MaxValue),
		new("File", 1.5f, 80f, float.MaxValue),
		new("Line", 0f, 50f, 70f),
		new("Suppression State", 0f, 120f, 140f),
	];

	private readonly PanelPart _part;
	private readonly Panel _toolbar;
	private readonly SelectBox _scope;
	private readonly SelectBox _source;
	private readonly SeverityToggle _errors;
	private readonly SeverityToggle _warnings;
	private readonly SeverityToggle _messages;
	private readonly TextEntry _search;
	private readonly GridHeader _header;
	private readonly ListPanel _list;
	private readonly float[] _widths = new float[Columns.Length];
	private readonly Dictionary<string, string?> _projects = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, string> _fileProjects = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

	private int[] _rows = [];
	private int _rowCount;
	private int _sortColumn = -1;
	private bool _sortDescending;

	public ProblemsView(PanelPart part) {
		_part = part;
		Name = "Problems";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_toolbar = new Toolbar { Name = "ErrorListToolbar", Dock = DockType.Top };
		_scope = new SelectBox(ScopeLabels) { Name = "Scope", Dock = DockType.Left, FontSize = Metrics.FontSizeLabel, Changed = _ => Sync() };
		_errors = new SeverityToggle(Codicon.Error, Theme.ProblemsErrorIcon, "Error", "Errors") { Dock = DockType.Left, Changed = Sync };
		_warnings = new SeverityToggle(Codicon.Warning, Theme.ProblemsWarningIcon, "Warning", "Warnings") { Dock = DockType.Left, Changed = Sync };
		_messages = new SeverityToggle(Codicon.Info, Theme.ProblemsInfoIcon, "Message", "Messages") { Dock = DockType.Left, Changed = Sync };
		_source = new SelectBox(SourceLabels) { Name = "Source", Dock = DockType.Left, FontSize = Metrics.FontSizeLabel, Changed = _ => Sync() };
		_search = new TextEntry { Name = "ErrorListSearch", Dock = DockType.Right, Placeholder = "Search Error List", FontSize = Metrics.FontSizeLabel, TextChanged = Sync };

		_toolbar.AddChild(_scope);
		_toolbar.AddChild(new Separator { Dock = DockType.Left });
		_toolbar.AddChild(_errors);
		_toolbar.AddChild(new Separator { Dock = DockType.Left });
		_toolbar.AddChild(_warnings);
		_toolbar.AddChild(new Separator { Dock = DockType.Left });
		_toolbar.AddChild(_messages);
		_toolbar.AddChild(new Separator { Dock = DockType.Left });
		_toolbar.AddChild(_source);
		_toolbar.AddChild(_search);

		_header = new GridHeader(this) { Name = "ErrorListHeader", Dock = DockType.Top };
		_list = new ListPanel { Name = "ErrorListRows", Dock = DockType.Fill, RowHeight = RowHeight, RowPainter = PaintRow, Activated = Activate };

		AddChild(_toolbar);
		AddChild(_header);
		AddChild(_list);
		Sync();
	}

	public ErrorListScope Scope {
		get => (ErrorListScope)_scope.Selected;
		set {
			_scope.Selected = (int)value;
			Sync();
		}
	}

	public ErrorListSource Source {
		get => (ErrorListSource)_source.Selected;
		set {
			_source.Selected = (int)value;
			Sync();
		}
	}

	public void Sync() {
		IReadOnlyList<Problem> problems = _part.Problems;

		if (_rows.Length < problems.Count) _rows = new int[Math.Max(problems.Count, _rows.Length * 2)];

		string? active = _part.ActiveFile?.Invoke();
		string? project = Scope == ErrorListScope.CurrentProject && active is not null ? ProjectFolder(active) : null;

		_open.Clear();

		if (Scope == ErrorListScope.OpenDocuments && _part.OpenFiles?.Invoke() is { } open) {
			foreach (string file in open) _open.Add(file);
		}

		ReadOnlySpan<char> search = _search.Text.Trim();
		int errors = 0, warnings = 0, infos = 0, count = 0;

		for (int i = 0; i < problems.Count; i++) {
			Problem problem = problems[i];

			if (!InScope(problem, active, project) || !MatchesSource(problem) || !MatchesSearch(problem, search)) continue;

			bool shown;

			switch (problem.Severity) {
				case DiagnosticSeverity.Error:
					errors++;
					shown = _errors.Checked;
					break;

				case DiagnosticSeverity.Warning:
					warnings++;
					shown = _warnings.Checked;
					break;

				default:
					infos++;
					shown = _messages.Checked;
					break;
			}

			if (shown) _rows[count++] = i;
		}

		_rowCount = count;
		_rows.AsSpan(0, count).Sort(Compare);

		_errors.SetCount(errors);
		_warnings.SetCount(warnings);
		_messages.SetCount(infos);

		_list.SetCount(count);
		_list.ClearSelection();
		_toolbar.InvalidateLayout();
		_header.Repaint();
		_list.Repaint();
	}

	private bool InScope(in Problem problem, string? active, string? project) => Scope switch {
		ErrorListScope.CurrentDocument => active is not null && string.Equals(problem.File, active, StringComparison.OrdinalIgnoreCase),
		ErrorListScope.OpenDocuments => _open.Contains(problem.File),
		ErrorListScope.CurrentProject => project is not null && IsUnder(problem.File, project),
		_ => true,
	};

	private bool MatchesSource(in Problem problem) => Source switch {
		ErrorListSource.BuildOnly => problem.Build,
		ErrorListSource.IntelliSenseOnly => !problem.Build,
		_ => true,
	};

	private bool MatchesSearch(in Problem problem, ReadOnlySpan<char> search) {
		if (search.IsEmpty) return true;

		return problem.Message.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase)
			|| (problem.Code is { } code && code.AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase))
			|| Path.GetFileName(problem.File.AsSpan()).Contains(search, StringComparison.OrdinalIgnoreCase)
			|| ProjectName(problem.File).AsSpan().Contains(search, StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsUnder(string file, string folder) =>
		file.Length > folder.Length && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && file[folder.Length] is '\\' or '/';

	private int Compare(int left, int right) {
		IReadOnlyList<Problem> problems = _part.Problems;
		Problem a = problems[left];
		Problem b = problems[right];

		int result = _sortColumn switch {
			1 => CompareText(a.Code, b.Code),
			2 => CompareText(a.Message, b.Message),
			3 => CompareText(ProjectName(a.File), ProjectName(b.File)),
			4 => Path.GetFileName(a.File.AsSpan()).CompareTo(Path.GetFileName(b.File.AsSpan()), StringComparison.OrdinalIgnoreCase),
			5 => a.Line.CompareTo(b.Line),
			_ => 0,
		};

		if (_sortDescending) result = -result;

		if (result == 0) result = a.Severity.CompareTo(b.Severity);

		if (result == 0) result = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);

		if (result == 0) result = a.Line.CompareTo(b.Line);

		return result == 0 ? a.Column.CompareTo(b.Column) : result;
	}

	private static int CompareText(string? left, string? right) => string.Compare(left ?? string.Empty, right ?? string.Empty, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

	private string? ProjectFolder(string file) {
		string? directory = Path.GetDirectoryName(file);
		string? root = _part.WorkspaceRoot;

		while (directory is not null) {
			if (ProjectIn(directory) is not null) return directory;

			if (root is null || !IsUnder(directory, root)) break;

			directory = Path.GetDirectoryName(directory);
		}

		return root;
	}

	private string ProjectName(string file) {
		if (_fileProjects.TryGetValue(file, out string? known)) return known;

		string name = ResolveProjectName(file);
		_fileProjects[file] = name;
		return name;
	}

	private string ResolveProjectName(string file) {
		string? directory = Path.GetDirectoryName(file);
		string? root = _part.WorkspaceRoot;

		while (directory is not null) {
			if (ProjectIn(directory) is { } name) return name;

			if (root is null || !IsUnder(directory, root)) break;

			directory = Path.GetDirectoryName(directory);
		}

		return root is null ? string.Empty : Path.GetFileName(root.AsSpan().TrimEnd(['\\', '/'])).ToString();
	}

	private string? ProjectIn(string directory) {
		if (_projects.TryGetValue(directory, out string? cached)) return cached;

		string? name = null;

		try {
			foreach (string entry in Directory.EnumerateFiles(directory)) {
				ReadOnlySpan<char> extension = Path.GetExtension(entry.AsSpan());

				if (!IsProjectExtension(extension)) continue;

				name = Path.GetFileNameWithoutExtension(entry.AsSpan()).ToString();
				break;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}

		_projects[directory] = name;
		return name;
	}

	private static bool IsProjectExtension(ReadOnlySpan<char> extension) =>
		extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
		|| extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)
		|| extension.Equals(".shproj", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sqlproj", StringComparison.OrdinalIgnoreCase)
		|| extension.Equals(".esproj", StringComparison.OrdinalIgnoreCase);

	private void Activate(int index) {
		if ((uint)index >= (uint)_rowCount) return;

		Problem problem = _part.Problems[_rows[index]];
		_part.Navigate?.Invoke(problem.File, problem.Line, problem.Column);
	}

	private void SortBy(int column) {
		if (column <= 0 || column > 5) return;

		_sortDescending = _sortColumn == column && !_sortDescending;
		_sortColumn = column;
		Sync();
	}

	private void LayoutColumns(float width, float scale) {
		float fixedWidth = 0f;
		float weights = 0f;

		for (int i = 0; i < Columns.Length; i++) {
			if (Columns[i].Weight == 0f) fixedWidth += Columns[i].Minimum * scale;
			else weights += Columns[i].Weight;
		}

		float flexible = MathF.Max(0f, width - fixedWidth);
		float leftover = flexible;
		float freeWeight = 0f;

		for (int i = 0; i < Columns.Length; i++) {
			Column column = Columns[i];

			if (column.Weight == 0f) {
				_widths[i] = column.Minimum * scale;
				continue;
			}

			float size = Math.Clamp(flexible * column.Weight / weights, column.Minimum * scale, column.Maximum * scale);
			_widths[i] = size;
			leftover -= size;

			if (column.Maximum == float.MaxValue) freeWeight += column.Weight;
		}

		if (MathF.Abs(leftover) < 0.5f || freeWeight == 0f) return;

		for (int i = 0; i < Columns.Length; i++) {
			if (Columns[i].Weight == 0f || Columns[i].Maximum != float.MaxValue) continue;

			_widths[i] = MathF.Max(Columns[i].Minimum * scale, _widths[i] + (leftover * Columns[i].Weight / freeWeight));
		}
	}

	private void PaintRow(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		if ((uint)index >= (uint)_rowCount) return;

		Problem problem = _part.Problems[_rows[index]];
		ITextFont font = painter.GetUi();
		float scale = Scale;
		float padding = CellPadding * scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float x = left;

		(Codicon icon, Color iconColor) = problem.Severity switch {
			DiagnosticSeverity.Error => (Codicon.Error, Theme.ProblemsErrorIcon),
			DiagnosticSeverity.Warning => (Codicon.Warning, Theme.ProblemsWarningIcon),
			_ => (Codicon.Info, Theme.ProblemsInfoIcon),
		};

		painter.DrawIcon(icon, x + padding + (Metrics.IconSize * scale / 2f), top + (height / 2f), Metrics.IconSize * scale, iconColor);
		x += _widths[0];

		Cell(painter, font, problem.Code, x, _widths[1], top, height, baseline, padding, Theme.TextLinkForeground);
		x += _widths[1];

		Cell(painter, font, problem.Message, x, _widths[2], top, height, baseline, padding, Theme.EditorForeground);
		x += _widths[2];

		Cell(painter, font, ProjectName(problem.File), x, _widths[3], top, height, baseline, padding, Theme.EditorForeground);
		x += _widths[3];

		Cell(painter, font, Path.GetFileName(problem.File.AsSpan()), x, _widths[4], top, height, baseline, padding, Theme.EditorForeground);
		x += _widths[4];

		Span<char> line = stackalloc char[11];
		problem.Line.TryFormat(line, out int digits);
		Cell(painter, font, line[..digits], x, _widths[5], top, height, baseline, padding, Theme.EditorForeground);
		x += _widths[5];

		Cell(painter, font, "Active", x, _widths[6], top, height, baseline, padding, Theme.DescriptionForeground);
	}

	private static void Cell(IPainter painter, ITextFont font, ReadOnlySpan<char> text, float x, float width, float top, float height, float baseline, float padding, Color color) {
		if (text.IsEmpty || width <= padding * 2f) return;

		float inner = width - (padding * 2f);

		if (font.Measure(text) <= inner) {
			painter.DrawText(font, text, x + padding, baseline, color);
			return;
		}

		float ellipsis = font.Measure("…");
		int length = text.Length;

		while (length > 0 && font.Measure(text[..length]) + ellipsis > inner) length--;

		painter.PushClip(x, top, width, height);
		painter.DrawText(font, text[..length], x + padding, baseline, color);
		painter.DrawText(font, "…", x + padding + font.Measure(text[..length]), baseline, color);
		painter.PopClip();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float control = ControlHeight * scale;
		float vertical = ((ToolbarHeight - ControlHeight) / 2f) * scale;
		float gap = ToolbarGap * scale;

		_toolbar.SetSize(Wide, ToolbarHeight * scale);
		_toolbar.DockPadding = new Thickness(ToolbarPadding * scale, 0f, ToolbarPadding * scale, scale);

		foreach (Panel child in _toolbar.Children) {
			switch (child) {
				case Separator separator:
					separator.SetSize(1f * scale, SeparatorHeight * scale);
					float separatorInset = ((ToolbarHeight - SeparatorHeight) / 2f) * scale;
					separator.DockMargin = new Thickness((SeparatorMargin * scale) + gap, separatorInset, SeparatorMargin * scale, separatorInset);
					break;

				case SeverityToggle toggle:
					toggle.SetSize(toggle.PreferredWidth, control);
					toggle.DockMargin = new Thickness(gap, vertical, 0f, vertical);
					break;
			}
		}

		_scope.SetSize(ScopeWidth * scale, control);
		_scope.DockMargin = new Thickness(0f, vertical, 0f, vertical);
		_source.SetSize(SourceWidth * scale, control);
		_source.DockMargin = new Thickness(gap, vertical, 0f, vertical);

		float used = (ToolbarPadding * 2f * scale) + _scope.Wide + _source.Wide + gap;

		foreach (Panel child in _toolbar.Children) {
			if (child is SeverityToggle or Separator) used += child.Wide + child.DockMargin.Left + child.DockMargin.Right;
		}

		float search = Math.Clamp(Wide - used - gap, SearchMinWidth * scale, SearchWidth * scale);
		_search.SetSize(search, control);
		_search.DockMargin = new Thickness(gap, vertical, 0f, vertical);

		_header.SetSize(Wide, HeaderHeight * scale);
		LayoutColumns(Wide, scale);

		base.PerformLayout();
	}

	private readonly record struct Column(string Label, float Weight, float Minimum, float Maximum);

	private sealed class Toolbar : Panel
	{
		public override void PaintBorder(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY + Tall - Scale, Wide, Scale, Theme.PanelBorder);
	}

	private sealed class Separator : Panel
	{
		public override void PaintBackground(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.PanelBorder);
	}

	private sealed class SeverityToggle : Panel
	{
		private const float Padding = 7f;
		private const float Gap = 5f;
		private const float UncheckedOpacity = 0.55f;

		private readonly Codicon _icon;
		private readonly Color _iconColor;
		private readonly string _one;
		private readonly string _many;
		private readonly char[] _label = new char[32];
		private int _length;

		public SeverityToggle(Codicon icon, Color iconColor, string one, string many) {
			Name = many;
			Cursor = CursorCode.Hand;
			_icon = icon;
			_iconColor = iconColor;
			_one = one;
			_many = many;
			SetCount(0);
		}

		public bool Checked { get; private set; } = true;

		public Action? Changed { get; set; }

		private ReadOnlySpan<char> Label => _label.AsSpan(0, _length);

		public float PreferredWidth => Ui is null ? 0f : ((Padding * 2f) + Metrics.IconSize + Gap) * Scale + Ui.GetFonts().GetUi().Measure(Label);

		public void SetCount(int count) {
			Span<char> buffer = _label;
			count.TryFormat(buffer, out int written);
			buffer[written++] = ' ';
			ReadOnlySpan<char> noun = count == 1 ? _one : _many;
			noun.CopyTo(buffer[written..]);
			int length = written + noun.Length;

			if (length == _length) {
				Repaint();
				_length = length;
				return;
			}

			_length = length;
			Parent?.InvalidateLayout();
			Repaint();
		}

		public override void OnCursorEntered() => Repaint();

		public override void OnCursorExited() => Repaint();

		public override bool OnMousePressed(MouseButton button, float x, float y) {
			if (button != MouseButton.Left) return false;

			Checked = !Checked;
			Repaint();
			Changed?.Invoke();
			return true;
		}

		public override void Paint(IPainter painter) {
			if (Ui is null) return;

			float scale = Scale;
			float radius = Metrics.RadiusSmall * scale;

			if (Checked) {
				painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.InputOptionActiveBackground);
				painter.StrokeRoundedRectangle(ScreenX + (scale / 2f), ScreenY + (scale / 2f), Wide - scale, Tall - scale, radius, scale, Theme.InputOptionActiveBorder);
			}
			else if (IsCursorOver) painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.ToolbarHoverBackground);

			float opacity = Checked ? 1f : UncheckedOpacity;
			ITextFont font = Ui.GetFonts().GetUi();
			float left = ScreenX + ((Padding + 1f) * scale);
			float centreY = ScreenY + (Tall / 2f);

			painter.DrawIcon(_icon, left + (Metrics.IconSize * scale / 2f), centreY, Metrics.IconSize * scale, _iconColor.WithAlpha(_iconColor.A * opacity));
			painter.DrawText(font, Label, left + ((Metrics.IconSize + Gap) * scale), centreY - (font.GetLineHeight() / 2f) + font.GetBaseline(), Theme.Foreground.WithAlpha(opacity));
		}
	}

	private sealed class GridHeader(ProblemsView owner) : Panel
	{
		private int _hover = -1;

		public override void OnCursorMoved(float x, float y) {
			int column = ColumnAt(x);

			if (column == _hover) return;

			_hover = column;
			Cursor = column is > 0 and <= 5 ? CursorCode.Hand : CursorCode.Arrow;
			Repaint();
		}

		public override void OnCursorExited() {
			_hover = -1;
			Repaint();
		}

		public override bool OnMousePressed(MouseButton button, float x, float y) {
			if (button != MouseButton.Left) return false;

			owner.SortBy(ColumnAt(x));
			return true;
		}

		private int ColumnAt(float x) {
			float left = 0f;

			for (int i = 0; i < owner._widths.Length; i++) {
				if (x < left + owner._widths[i]) return i;

				left += owner._widths[i];
			}

			return -1;
		}

		public override void Paint(IPainter painter) {
			if (Ui is null) return;

			ITextFont font = Ui.GetFonts().GetUi();
			float scale = Scale;
			float padding = CellPadding * scale;
			float baseline = ScreenY + ((Tall - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float x = ScreenX;

			for (int i = 0; i < Columns.Length; i++) {
				float width = owner._widths[i];
				bool sortable = i is > 0 and <= 5;

				if (sortable && i == _hover) painter.FillRectangle(x, ScreenY, width, Tall, Theme.ToolbarHoverBackground);

				Color color = sortable && i == _hover ? Theme.Foreground : Theme.DescriptionForeground;

				painter.PushClip(x, ScreenY, width, Tall);
				painter.DrawText(font, Columns[i].Label, x + padding, baseline, color);

				if (i == owner._sortColumn) {
					ITextFont arrow = Ui.GetFonts().GetUi(10f, false);
					painter.DrawText(arrow, owner._sortDescending ? "▾" : "▴", x + padding + font.Measure(Columns[i].Label) + (6f * scale), baseline, color);
				}

				painter.PopClip();
				x += width;
			}
		}
	}
}
