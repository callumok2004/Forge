using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;
using Forge.VSCode;

namespace Forge.Shell;

public sealed class AuxiliaryBar
{
	public const float MinWidth = 240f;
	public const float MaxWidth = 900f;

	private IReadOnlyList<WebviewViewEntry> _views = [];

	public bool IsOpen { get; set; }

	public float Width { get; set; } = 380f;

	public string? ActiveViewId { get; private set; }

	public string? Status { get; set; }

	public PixelRect ContentBounds { get; private set; }

	public IReadOnlyList<WebviewViewEntry> Views => _views;

	public bool HasViews => _views.Count > 0;

	public const string Location = "secondarySidebar";

	public void SetViews(IReadOnlyList<WebviewViewEntry> views) {
		List<WebviewViewEntry> mine = [];

		foreach (WebviewViewEntry view in views) {
			if (string.Equals(view.Location, Location, StringComparison.Ordinal)) mine.Add(view);
		}

		_views = mine;
		views = mine;

		if (ActiveViewId is { } active && Find(active) is not null) return;

		ActiveViewId = views.Count > 0 ? views[0].ViewId : null;
	}

	public WebviewViewEntry? Active => ActiveViewId is { } id ? Find(id) : null;

	private readonly Dictionary<string, string> _html = new(StringComparer.Ordinal);
	private readonly HashSet<string> _requested = new(StringComparer.Ordinal);

	public string? ShownViewId { get; set; }

	public string? HtmlFor(string viewId) => _html.TryGetValue(viewId, out string? html) ? html : null;

	public void SetHtml(string viewId, string html) => _html[viewId] = html;

	public bool ClaimResolve(string viewId) => _requested.Add(viewId);

	public WebviewViewEntry? Find(string viewId) {
		foreach (WebviewViewEntry view in _views) {
			if (string.Equals(view.ViewId, viewId, StringComparison.Ordinal)) return view;
		}

		return null;
	}

	public bool Select(string viewId) {
		if (Find(viewId) is null || string.Equals(ActiveViewId, viewId, StringComparison.Ordinal)) return false;

		ActiveViewId = viewId;
		return true;
	}

	public void RestoreSelection(string viewId) => ActiveViewId = viewId;

	public void Resize(float width) => Width = Math.Clamp(width, MinWidth, MaxWidth);

	public bool Toggle() {
		if (!HasViews) return false;

		IsOpen = !IsOpen;
		return true;
	}

	public PixelRect CloseBounds { get; private set; }

	public bool IsOverClose(float x, float y) =>
		IsOpen && x >= CloseBounds.Left && x < CloseBounds.Right && y >= CloseBounds.Top &&
		y < CloseBounds.Bottom;

	public bool HoverClose { get; set; }

	public int TabAt(float x, float y, float left, float top, float width, float scale) {
		float headerHeight = Metrics.SidebarHeaderHeight * scale;

		if (y < top || y >= top + headerHeight || x < left || x >= left + width) return -1;

		float cursor = left + (Metrics.Space12 * scale);

		for (int i = 0; i < _views.Count && i < _tabWidths.Count; i++) {
			if (x >= cursor && x < cursor + _tabWidths[i]) return i;

			cursor += _tabWidths[i] + (Metrics.Space12 * scale);
		}

		return -1;
	}

	private readonly List<float> _tabWidths = [];

	public void Render(IPainter painter, float x, float y, float width, float height, float scale) {
		float inner = Metrics.CardInnerMargin * scale;
		float cardWidth = Math.Max(0f, width - inner);
		float cardHeight = Math.Max(0f, height - inner);
		float radius = Metrics.RadiusLarge * scale;
		float headerHeight = Metrics.SidebarHeaderHeight * scale;

		painter.FillRoundedRectangle(x, y, cardWidth, cardHeight, radius, Theme.ChromeBackground);
		painter.FillRectangle(x, y + cardHeight - radius, cardWidth, radius, Theme.ChromeBackground);

		painter.StrokeRoundedRectangle(x, y, cardWidth, cardHeight, radius,
			Metrics.StrokeThickness * scale, Theme.PanelBorder);

		ITextFont font = painter.GetUiStrong();
		float baseline = y + ((headerHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float cursor = x + (Metrics.Space12 * scale);

		_tabWidths.Clear();

		foreach (WebviewViewEntry view in _views) {
			string label = view.Title;
			float measured = font.Measure(label);

			_tabWidths.Add(measured);

			bool active = string.Equals(view.ViewId, ActiveViewId, StringComparison.Ordinal);

			painter.DrawText(font, label, cursor, baseline,
				active ? Theme.SidebarTitleForeground : Theme.SectionHeaderForeground);

			cursor += measured + (Metrics.Space12 * scale);
		}

		float action = Metrics.IconSize * scale;
		float closeCentreX = x + cardWidth - (Metrics.Space12 * scale) - (action / 2f);
		float closeCentreY = y + (headerHeight / 2f);
		float box = 20f * scale;

		if (HoverClose) {
			painter.FillRoundedRectangle(closeCentreX - (box / 2f), closeCentreY - (box / 2f), box, box,
				Metrics.RadiusSmall * scale, Theme.ListHoverBackground);
		}

		painter.DrawIcon(Codicon.Close, closeCentreX, closeCentreY, action,
			HoverClose ? Theme.TabActiveForeground : Theme.SectionHeaderForeground);

		CloseBounds = new PixelRect((int)(closeCentreX - (box / 2f)), (int)(closeCentreY - (box / 2f)),
			(int)(closeCentreX + (box / 2f)), (int)(closeCentreY + (box / 2f)));

		float contentTop = y + headerHeight;

		float stroke = Metrics.StrokeThickness * scale;

		ContentBounds = new PixelRect(
			(int)MathF.Ceiling(x + stroke),
			(int)MathF.Ceiling(contentTop),
			(int)MathF.Floor(x + cardWidth - stroke),
			(int)MathF.Floor(y + cardHeight));

		if (Status is not { Length: > 0 } status) return;

		ITextFont body = painter.GetUi();

		painter.DrawText(body, status, x + (Metrics.Space12 * scale),
			contentTop + (Metrics.Space12 * scale) + body.GetBaseline(), Theme.SectionHeaderForeground);
	}
}
