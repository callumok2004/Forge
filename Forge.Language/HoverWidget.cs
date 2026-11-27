using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Language;

public sealed class HoverWidget : Panel
{
	public const long DelayMilliseconds = 300;

	public const long HidingDelayMilliseconds = 300;

	private const float ContentPaddingX = 8f;
	private const float ContentPaddingY = 4f;
	private const float MarkdownMaxWidth = 500f;
	private const float MinimumMaxWidth = 750f;
	private const float EditorWidthShare = 0.66f;
	private const float FontSize = 13f;
	private const float LineHeightEm = 1.5f;
	private const float ParagraphGap = 8f;
	private const float DetailsGap = 6f;
	private const float DetailsOpacity = 0.6f;
	private const float StatusFontSize = 12f;
	private const float StatusLineHeight = 22f;
	private const float StatusActionGap = 16f;
	private const float Radius = 8f;
	private const float ShadowBlur = 12f;
	private const int ShadowSteps = 6;
	private const long QuickFixPlaceholderDelay = 200;

	private const float CellGap = 12f;

	public enum QuickFixState : byte
	{
		None,
		Pending,
		Checking,
		Available,
		Unavailable,
	}

	public readonly record struct Marker(int Line, int StartColumn, int EndColumn, string Message, DiagnosticSeverity Severity, string? Code, string? Href);

	private readonly record struct Link(float X, float Y, float Width, float Height, string? Href, int Action);

	private List<List<MarkdownBlock>> _lines = [];
	private HoverMarkdownLayout? _markdown;
	private readonly List<Marker> _markers = [];
	private readonly List<(float Top, float Height, bool Status)> _rows = [];
	private readonly List<(float X, float Y, float Width, Color Color)> _underlines = [];
	private readonly List<(float X, float Y, string Text, ITextFont Font, Color Color)> _labels = [];
	private readonly List<Link> _links = [];
	private readonly TextSelection _selection = new();

	private float _primaryX;
	private float _secondaryX;
	private float _lineTop;
	private float _lineHeight;
	private float _contentHeight;
	private float _scroll;
	private bool _dirty;
	private bool _selecting;
	private int _hoveredLink = -1;
	private long _quickFixSince;
	private string? _actionKeyQuickFix = "Ctrl+.";
	private string? _actionKeyViewProblem = "Alt+F8";

	public HoverWidget() {
		Name = "Hover";
		Visible = false;
		MouseInputEnabled = true;
	}

	public int Line { get; private set; }

	public int Column { get; private set; }

	public bool IsOpen { get; private set; }

	public QuickFixState QuickFix { get; private set; }

	public IReadOnlyList<Marker> Markers => _markers;

	public Action<Marker, float, float>? QuickFixRequested { get; set; }

	public Action<Marker>? ViewProblemRequested { get; set; }

	public Action<string>? LinkRequested { get; set; }

	public Action? Changed { get; set; }

	public void SetKeybindings(string? quickFix, string? viewProblem) {
		_actionKeyQuickFix = quickFix;
		_actionKeyViewProblem = viewProblem;
		_dirty = true;
	}

	public void Aim(int line, int column, float x, float y) {
		Line = line;
		Column = column;
		_primaryX = x;
	}

	public void Anchor(float primaryX, float secondaryX, float lineTop, float lineHeight) {
		_primaryX = primaryX;
		_secondaryX = secondaryX;
		_lineTop = lineTop;
		_lineHeight = lineHeight;
	}

	public void ShowMarkers(IReadOnlyList<Marker> markers, int line, int column) {
		if (line != Line || column != Column) return;

		lock (_markers) {
			_markers.Clear();
			_markers.AddRange(markers);
			_markers.Sort(static (a, b) => a.Severity.CompareTo(b.Severity));
			QuickFix = markers.Count > 0 ? QuickFixState.Pending : QuickFixState.None;
			_quickFixSince = Environment.TickCount64;
			_dirty = true;
			_scroll = 0f;
			IsOpen = _markers.Count > 0 || _lines.Count > 0;
		}
	}

	public void SetQuickFixes(bool available, int line, int column) {
		if (line != Line || column != Column || _markers.Count == 0) return;

		QuickFix = available ? QuickFixState.Available : QuickFixState.Unavailable;
		_dirty = true;
	}

	public bool AdvanceQuickFixPlaceholder() {
		if (QuickFix != QuickFixState.Pending || Environment.TickCount64 - _quickFixSince < QuickFixPlaceholderDelay) return false;

		QuickFix = QuickFixState.Checking;
		_dirty = true;
		return true;
	}

	public void Show(string markdown, int line, int column) {
		if (line != Line || column != Column || markdown.Length == 0) return;

		lock (_markers) {
			_lines = HoverMarkdown.ParseRows(markdown);
			_dirty = true;
			IsOpen = _markers.Count > 0 || _lines.Count > 0;
		}
	}

	public void Hide() {
		if (!IsOpen && _markers.Count == 0 && _lines.Count == 0) return;

		lock (_markers) {
			IsOpen = false;
			_markers.Clear();
			_lines.Clear();
			QuickFix = QuickFixState.None;
			_selection.Clear();
			_selecting = false;
			_hoveredLink = -1;
			_scroll = 0f;
			_dirty = true;
		}

		if (HasFocus) Ui?.SetFocus(null);
	}

	public bool Contains(float screenX, float screenY) => Visible && screenX >= ScreenX && screenX < ScreenX + Wide && screenY >= ScreenY && screenY < ScreenY + Tall;

	public bool IsSelecting => _selecting || _selection.HasSelection;

	public void Place(float areaTop, float areaBottom, float areaRight, float originX, float originY, float editorWidth) {
		Visible = IsOpen;

		if (!IsOpen || Ui is null) return;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		float border = MathF.Max(1f, scale);
		float maxWidth = MathF.Min(MathF.Max(editorWidth * EditorWidthShare, MinimumMaxWidth * scale), Ui.GetViewportWidth() - (14f * scale));

		if (_dirty || MathF.Abs(maxWidth - _laidOutFor) > 0.5f) {
			_dirty = false;
			_laidOutFor = maxWidth;

			lock (_markers) Layout(fonts, maxWidth - (border * 2f), scale);
		}

		float width = _contentWidth + (border * 2f);
		float height = _contentHeight + (border * 2f);
		float spaceAbove = _lineTop - areaTop;
		float spaceBelow = areaBottom - (_lineTop + _lineHeight);
		float limit = MathF.Max(spaceAbove, spaceBelow);
		float shown = MathF.Min(height, limit);
		bool above = shown <= spaceAbove;
		float character = fonts.GetCode().GetCellWidth();
		float left = _secondaryX < _primaryX ? MathF.Max(_secondaryX, _primaryX - width + character) : MathF.Min(_secondaryX, _primaryX + width - character);

		left = Math.Clamp(left, 0f, MathF.Max(0f, Ui.GetViewportWidth() - width));

		float top = above ? _lineTop - shown : _lineTop + _lineHeight;

		_scroll = Math.Clamp(_scroll, 0f, MathF.Max(0f, height - shown));
		SetBounds(left - originX, top - originY, width, shown);
	}

	private float _laidOutFor = -1f;
	private float _contentWidth;

	private void Layout(IFontSet fonts, float maxInner, float scale) {
		_selection.Clear();
		_labels.Clear();
		_links.Clear();
		_rows.Clear();
		_underlines.Clear();
		_hoveredLink = -1;

		ITextFont code = fonts.GetCode();
		ITextFont ui = fonts.GetUi(FontSize);
		ITextFont status = fonts.GetUi(StatusFontSize);
		float padX = ContentPaddingX * scale;
		float padY = ContentPaddingY * scale;
		float border = MathF.Max(1f, scale);
		float uiLine = MathF.Round(FontSize * LineHeightEm * scale);
		float y = 0f;
		float widest = 0f;
		List<string> pieces = [];

		foreach (Marker marker in _markers) {
			float top = y;
			float lineHeight = code.GetLineHeight();
			float wrap = maxInner - (padX * 2f);
			string details = marker.Code is { Length: > 0 } value ? $"({value})" : string.Empty;
			string[] paragraphs = marker.Message.Replace("\r\n", "\n").Split('\n');
			float rowY = top + padY;
			float x = padX;

			for (int p = 0; p < paragraphs.Length; p++) {
				pieces.Clear();
				SuggestDetails.Wrap(code, paragraphs[p], wrap, pieces);

				if (pieces.Count == 0) pieces.Add(string.Empty);

				for (int i = 0; i < pieces.Count; i++) {
					_selection.Add(new TextRun(padX, rowY, pieces[i], code, Theme.EditorHoverForeground, i > 0));
					x = padX + code.Measure(pieces[i]);
					widest = MathF.Max(widest, x);

					if (p < paragraphs.Length - 1 || i < pieces.Count - 1) rowY += lineHeight;
				}
			}

			if (details.Length > 0) {
				float gap = DetailsGap * scale;
				float detailsWidth = code.Measure(details);

				if (x + gap + detailsWidth > padX + wrap) {
					rowY += lineHeight;
					x = padX;
					gap = 0f;
				}

				float detailsX = x + gap;
				Color detailsColor = Theme.EditorHoverForeground.Transparency(DetailsOpacity);

				if (marker.Href is { Length: > 0 } href) {
					float open = code.Measure("(");
					float codeWidth = code.Measure(marker.Code!);
					Color linkColor = Theme.TextLinkForeground.Transparency(DetailsOpacity);

					_selection.Add(new TextRun(detailsX, rowY, "(", code, detailsColor, true));
					_selection.Add(new TextRun(detailsX + open, rowY, marker.Code!, code, linkColor, true));
					_selection.Add(new TextRun(detailsX + open + codeWidth, rowY, ")", code, detailsColor, true));
					_underlines.Add((detailsX + open, rowY + code.GetBaseline() + MathF.Max(1f, scale) * 2f, codeWidth, linkColor));
					_links.Add(new Link(detailsX + open, rowY, codeWidth, lineHeight, href, 0));
				}
				else _selection.Add(new TextRun(detailsX, rowY, details, code, detailsColor, true));

				widest = MathF.Max(widest, detailsX + detailsWidth);
			}

			y = rowY + lineHeight + padY;
			_rows.Add((top, y - top, false));
		}

		if (_lines.Count > 0) {
			float wrap = MathF.Min(MarkdownMaxWidth * scale, maxInner - (padX * 2f));
			_markdown = new HoverMarkdownLayout(fonts, scale, _selection);
			List<string> hrefs = [];

			foreach (List<MarkdownBlock> row in _lines) {
				float top = y;
				y = _markdown.Layout(row, padX, wrap, top + padY, 0f, hrefs) + padY;
				_rows.Add((top, y - top, false));
			}

			widest = MathF.Max(widest, _markdown.Widest);

			foreach (HoverMarkdownLayout.LinkBox link in _markdown.Links) _links.Add(new Link(link.X, link.Y, link.Width, link.Height, link.Href, 0));
		}
		else _markdown = null;

		if (_markers.Count > 0) {
			float top = y;
			float x = padX;
			float labelY = top + ((StatusLineHeight * scale) - status.GetLineHeight()) / 2f;
			float actionGap = StatusActionGap * scale;

			x = AddAction(status, x, labelY, Label("View Problem", _actionKeyViewProblem), 1) + actionGap;

			switch (QuickFix) {
				case QuickFixState.Available:
					x = AddAction(status, x, labelY, Label("Quick Fix...", _actionKeyQuickFix), 2) + actionGap;
					break;

				case QuickFixState.Checking:
					_labels.Add((x, labelY, "Checking for quick fixes...", status, Theme.EditorHoverForeground));
					x += status.Measure("Checking for quick fixes...") + actionGap;
					break;

				case QuickFixState.Unavailable:
					_labels.Add((x, labelY, "No quick fixes available", status, Theme.EditorHoverForeground));
					x += status.Measure("No quick fixes available") + actionGap;
					break;
			}

			widest = MathF.Max(widest, x - actionGap + padX);
			y += StatusLineHeight * scale;
			_rows.Add((top, StatusLineHeight * scale, true));
		}

		_contentWidth = MathF.Min(maxInner, widest + padX);
		_contentHeight = y + (border * 0f);
	}

	private static string Label(string text, string? key) => key is { Length: > 0 } ? $"{text} ({key})" : text;

	private float AddAction(ITextFont font, float x, float y, string text, int action) {
		float width = font.Measure(text);
		_labels.Add((x, y, text, font, Theme.TextLinkForeground));
		_links.Add(new Link(x, y, width, font.GetLineHeight(), null, action));
		return x + width;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float radius = Radius * scale;
		float border = MathF.Max(1f, scale);
		float blur = ShadowBlur * scale;

		for (int step = ShadowSteps; step >= 1; step--) {
			float spread = blur * step / ShadowSteps;
			painter.FillRoundedRectangle(ScreenX - spread, ScreenY - spread, Wide + (spread * 2f), Tall + (spread * 2f), radius + spread, Theme.HoverShadow.Transparency(1f / ShadowSteps));
		}

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.EditorHoverBackground);
		painter.PushRoundedClip(ScreenX + border, ScreenY + border, Wide - (border * 2f), Tall - (border * 2f), MathF.Max(0f, radius - border));

		float originX = ScreenX + border;
		float originY = ScreenY + border - _scroll;
		Color separator = Theme.EditorHoverBorder.Transparency(0.5f);

		for (int i = 0; i < _rows.Count; i++) {
			(float top, float height, bool status) = _rows[i];

			if (status) painter.FillRectangle(originX, originY + top, Wide - (border * 2f), height, Theme.EditorHoverStatusBarBackground);

			if (i > 0) painter.FillRectangle(originX, originY + top, Wide - (border * 2f), border, separator);
		}

		if (_markdown is { } markdown) {
			foreach (HoverMarkdownLayout.BoxDraw box in markdown.Boxes) painter.FillRoundedRectangle(originX + box.X, originY + box.Y, box.Width, box.Height, box.Radius, box.Color);
		}

		_selection.Paint(painter, originX, originY, ScreenY, ScreenY + Tall, HasFocus);

		if (_markdown is { } content) {
			string? hoveredHref = _hoveredLink >= 0 ? _links[_hoveredLink].Href : null;

			foreach (HoverMarkdownLayout.LinkText link in content.LinkTexts) {
				bool active = hoveredHref is not null && link.Href == hoveredHref;
				painter.DrawText(link.Font, link.Text, originX + link.X, originY + link.Y + link.Font.GetBaseline(), active ? Theme.TextLinkActiveForeground : Theme.TextLinkForeground);
			}

			foreach (HoverMarkdownLayout.IconDraw icon in content.Icons) {
				bool active = icon.Href is not null && icon.Href == hoveredHref;
				painter.DrawIcon(icon.Icon, originX + icon.X + (icon.Size / 2f), originY + icon.Y + (icon.Size / 2f), icon.Size, active ? Theme.TextLinkActiveForeground : icon.Color);
			}

			foreach (HoverMarkdownLayout.ImageDraw image in content.Images) painter.DrawImage(image.Image, originX + image.X, originY + image.Y, image.Width, image.Height, false);

			foreach (HoverMarkdownLayout.RuleDraw rule in content.Rules) painter.FillRectangle(originX, originY + rule.Y, Wide - (border * 2f), border, separator);
		}

		foreach ((float x, float y, float width, Color color) in _underlines) painter.FillRectangle(originX + x, originY + y, width, border, color);

		for (int i = 0; i < _labels.Count; i++) {
			(float x, float y, string text, ITextFont font, Color color) = _labels[i];

			if (text.Length == 0) {
				painter.FillRectangle(originX, originY + y, Wide - (border * 2f), border, color);
				continue;
			}

			bool hovered = _hoveredLink >= 0 && _links[_hoveredLink] is { Href: null } link && MathF.Abs(link.X - x) < 0.5f && MathF.Abs(link.Y - y) < 0.5f;
			painter.DrawText(font, text, originX + x, originY + y + font.GetBaseline(), hovered ? Theme.TextLinkActiveForeground : color);
		}

		painter.PopRoundedClip();
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, border, Theme.EditorHoverBorder);
	}

	private (float X, float Y) Content(float x, float y) {
		float border = MathF.Max(1f, Scale);
		return (x - border, y - border + _scroll);
	}

	private int LinkAt(float x, float y) {
		(float cx, float cy) = Content(x, y);

		for (int i = 0; i < _links.Count; i++) {
			Link link = _links[i];

			if (cx >= link.X && cx < link.X + link.Width && cy >= link.Y && cy < link.Y + link.Height) return i;
		}

		return -1;
	}

	public override void OnCursorMoved(float x, float y) {
		(float cx, float cy) = Content(x, y);

		if (_selecting) {
			_selection.Extend(cx, cy);
			Repaint();
			return;
		}

		int link = LinkAt(x, y);

		if (link != _hoveredLink) {
			_hoveredLink = link;
			Repaint();
		}

		Cursor = link >= 0 ? CursorCode.Hand : _selection.OverText(cx, cy) ? CursorCode.IBeam : CursorCode.Arrow;
	}

	public override void OnCursorExited() {
		if (_hoveredLink < 0) return;

		_hoveredLink = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left) return true;

		int index = LinkAt(x, y);

		if (index >= 0) {
			Link link = _links[index];
			Marker? marker = _markers.Count > 0 ? _markers[0] : null;

			switch (link.Action) {
				case 0 when link.Href is { } href:
					LinkRequested?.Invoke(href);
					break;

				case 1 when marker is { } viewed:
					Hide();
					ViewProblemRequested?.Invoke(viewed);
					break;

				case 2 when marker is { } fixedMarker:
					float border = MathF.Max(1f, Scale);
					float anchorX = ScreenX + border + link.X;
					float anchorY = ScreenY + border + link.Y - _scroll;
					Hide();
					QuickFixRequested?.Invoke(fixedMarker, anchorX, anchorY);
					break;
			}

			Changed?.Invoke();
			return true;
		}

		(float cx, float cy) = Content(x, y);
		RequestFocus();
		_selecting = true;
		_selection.Begin(cx, cy, (Ui?.GetModifiers() & KeyModifiers.Shift) != 0);
		Ui?.SetMouseCapture(this);
		Repaint();
		return true;
	}

	public override bool OnMouseDoublePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || LinkAt(x, y) >= 0) return OnMousePressed(button, x, y);

		(float cx, float cy) = Content(x, y);
		_selection.SelectWord(cx, cy);
		Repaint();
		return true;
	}

	public override bool OnMouseTriplePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || LinkAt(x, y) >= 0) return OnMousePressed(button, x, y);

		(float cx, float cy) = Content(x, y);
		_selection.SelectLine(cx, cy);
		Repaint();
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (!_selecting) return true;

		_selecting = false;
		Ui?.SetMouseCapture(null);
		return true;
	}

	public override void OnMouseCaptureLost() => _selecting = false;

	public override bool OnMouseWheeled(int delta, float x, float y) {
		float border = MathF.Max(1f, Scale);
		float overflow = _contentHeight + (border * 2f) - Tall;

		if (overflow <= 0f) return true;

		_scroll = Math.Clamp(_scroll - (delta / 120f * 3f * (Ui?.GetFonts().GetCode().GetLineHeight() ?? 16f)), 0f, overflow);
		Repaint();
		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if ((modifiers & KeyModifiers.Control) != 0) {
			switch (key) {
				case VirtualKey.C when _selection.HasSelection:
					Clipboard.SetText(_selection.SelectedText());
					return true;

				case VirtualKey.A:
					_selection.SelectAll();
					Repaint();
					return true;
			}
		}

		if (key != VirtualKey.Escape) return false;

		Hide();
		Changed?.Invoke();
		return true;
	}

}
