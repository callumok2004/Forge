using Forge.Common.Render;
using Forge.Theming;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class TabStrip
{
	private const float MinWidth = 110f;
	private const float MaxWidth = 240f;
	private const float CloseSlot = 24f;

	private int _hoverIndex = -1;
	private bool _hoverClose;

	public static float Height(float scale) =>
		(Metrics.TabHeight + (Metrics.TabStripPadding * 2f)) * scale;

	public int HoverIndex {
		get => _hoverIndex;
		set => _hoverIndex = value;
	}

	public bool HoverClose {
		get => _hoverClose;
		set => _hoverClose = value;
	}

	public int IndexAt(IReadOnlyList<Document> documents, IPainter painter, float x, float y, float stripX,
		float stripTop, float scale) {
		if (y < stripTop || y > stripTop + Height(scale) || x < stripX) return -1;

		float left = stripX + (Metrics.TabStripPadding * scale);
		for (int i = 0; i < documents.Count; i++) {
			float width = TabWidth(documents[i], painter, scale);
			if (x >= left && x < left + width) return i;

			left += width + (Metrics.TabGap * scale);
		}

		return -1;
	}

	public static bool IsOverClose(IReadOnlyList<Document> documents, IPainter painter, float x, float stripX, float scale,
		int index) {
		float left = stripX + (Metrics.TabStripPadding * scale);
		for (int i = 0; i < index; i++) left += TabWidth(documents[i], painter, scale) + (Metrics.TabGap * scale);

		float width = TabWidth(documents[index], painter, scale);
		return x >= left + width - (CloseSlot * scale);
	}

	private static float TabWidth(Document document, IPainter painter, float scale) {
		float text = painter.GetUiStrong().Measure(document.Name);

		float width = text + (Metrics.Space8 * scale) + (Metrics.IconSize * scale) +
			(Metrics.Space6 * scale) + (CloseSlot * scale);

		return Math.Clamp(width, MinWidth * scale, MaxWidth * scale);
	}

	public void Render(IPainter painter, IReadOnlyList<Document> documents, int activeIndex, float x, float y,
		float width, float scale) {
		ITextFont font = painter.GetUiStrong();
		float height = Height(scale);
		float padding = Metrics.TabStripPadding * scale;
		float pillHeight = Metrics.TabHeight * scale;
		float pillTop = y + padding;
		float radius = Metrics.RadiusSmall * scale;

		painter.PushClip(x, y, width, height);

		float left = x + padding;
		for (int i = 0; i < documents.Count && left < x + width; i++) {
			Document document = documents[i];
			float tabWidth = TabWidth(document, painter, scale);

			bool isActive = i == activeIndex;
			bool isHovered = i == _hoverIndex;

			float tint = isActive ? 0.18f : isHovered ? 0.08f : 0.05f;
			painter.FillRoundedRectangle(left, pillTop, tabWidth, pillHeight, radius,
				Theme.EditorForeground.WithAlpha(tint));

			float baseline = pillTop + ((pillHeight - font.GetLineHeight()) / 2f) + font.GetBaseline();
			float centreY = pillTop + (pillHeight / 2f);
			Color color = isActive ? Theme.TabActiveForeground : Theme.TabInactiveForeground;

			float cursor = left + (Metrics.Space8 * scale);

			float iconSize = Metrics.IconSize * scale;
			float iconCentre = cursor + (iconSize / 2f);

			if (painter.DrawFileIcon(document.Name, iconCentre, centreY, iconSize)) {
			}
			else {
				painter.DrawIcon(FileIcon(document.Name), iconCentre, centreY, iconSize,
					FileTypeChip(document.Name, isActive));
			}

			cursor += (Metrics.IconSize + Metrics.Space6) * scale;

			painter.PushClip(left, pillTop, tabWidth - (CloseSlot * scale), pillHeight);
			painter.DrawText(font, document.Name, cursor, baseline, color);
			painter.PopClip();

			float markX = left + tabWidth - (CloseSlot * scale) + (8f * scale);

			if (document.IsDirty() && !(isHovered && _hoverClose)) {
				painter.FillCircle(markX, centreY, 4f * scale, Theme.Accent);
			}
			else if (isActive || isHovered) {
				if (isHovered && _hoverClose) {
					painter.FillRoundedRectangle(markX - (9f * scale), centreY - (9f * scale), 18f * scale,
						18f * scale, radius, Theme.ListHoverBackground);
				}

				painter.DrawIcon(Codicon.Close, markX, centreY, 14f * scale, color);
			}

			left += tabWidth + (Metrics.TabGap * scale);
		}

		painter.PopClip();
	}

	private static Codicon FileIcon(string name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return Codicon.Json;

		return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? Codicon.Markdown : Codicon.File;
	}

	private static Color FileTypeChip(string name, bool isActive) {
		ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());

		Color color =
			extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#B392F0") :
			extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#FFAB70") :
			extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#79B8FF") :
			extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#6BD68A") :
			extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#F9E27D") :
			Theme.SectionHeaderForeground;

		return isActive ? color : color.WithAlpha(0.7f);
	}
}
