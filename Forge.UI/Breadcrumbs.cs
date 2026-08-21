using Forge.Common.Render;
using Forge.Theming;

namespace Forge.UI;

public sealed class Breadcrumbs
{
	private const int MaxSegments = 6;

	private readonly string[] _segments = new string[MaxSegments];
	private int _count;

	public static float Height(float scale) => Metrics.BreadcrumbHeight * scale;

	public void SetPath(string? rootPath, string? fullPath) {
		_count = 0;

		if (string.IsNullOrEmpty(fullPath)) return;

		string relative = string.IsNullOrEmpty(rootPath)
			? Path.GetFileName(fullPath)
			: Path.GetRelativePath(rootPath, fullPath);

		ReadOnlySpan<char> remaining = relative;

		Span<Range> parts = stackalloc Range[32];
		int partCount = 0;

		int start = 0;
		for (int i = 0; i < remaining.Length && partCount < parts.Length; i++) {
			if (remaining[i] is '/' or '\\') {
				parts[partCount++] = new Range(start, i);
				start = i + 1;
			}
		}

		if (partCount < parts.Length) parts[partCount++] = new Range(start, remaining.Length);

		int first = Math.Max(0, partCount - MaxSegments);
		for (int i = first; i < partCount; i++) _segments[_count++] = remaining[parts[i]].ToString();
	}

	public void Render(IPainter painter, float x, float y, float width, float scale) {
		float height = Height(scale);
		painter.FillRectangle(x, y, width, height, Theme.EditorBackground);

		if (_count == 0) return;

		ITextFont font = painter.GetUi();
		float baseline = y + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float centreY = y + (height / 2f);
		float cursor = x + (Metrics.SpaceMd * scale);

		painter.PushClip(x, y, width, height);

		for (int i = 0; i < _count; i++) {
			bool isLast = i == _count - 1;

			if (i == 0) {
				float iconSize = Metrics.IconSize * scale;

				painter.DrawIcon(Codicon.Folder, cursor + (iconSize / 2f), centreY, iconSize,
					Theme.BreadcrumbForeground);

				cursor += iconSize + (Metrics.Space4 * scale);
			}

			Color color = isLast ? Theme.BreadcrumbActiveForeground : Theme.BreadcrumbForeground;
			painter.DrawText(font, _segments[i], cursor, baseline, color);
			cursor += font.Measure(_segments[i]);

			if (!isLast) {
				float chevron = 14f * scale;

				cursor += Metrics.Space4 * scale;
				painter.DrawIcon(Codicon.ChevronRight, cursor + (chevron / 2f), centreY, chevron,
					Theme.BreadcrumbForeground);

				cursor += chevron + (Metrics.Space4 * scale);
			}
		}

		painter.PopClip();
	}
}
