using Forge.Common.Render;
using Forge.Core;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.Shell;

public sealed class ProcessExplorer(ProcessTree tree)
{
	private const float Width = 640f;
	private const float RowHeight = 22f;
	private const float HeaderHeight = 30f;
	private const float Padding = 12f;
	private const float CpuColumn = 80f;
	private const float MemoryColumn = 110f;
	private const float PidColumn = 80f;

	private float _left;
	private float _top;
	private float _width;
	private float _height;

	public bool IsOpen { get; private set; }

	public void Toggle() => IsOpen = !IsOpen;

	public void Close() => IsOpen = false;

	public PixelRect Bounds => IsOpen ? PixelRect.Around(_left - 2f, _top - 2f, _width + 4f, _height + 4f) : PixelRect.Empty;

	public bool Contains(float x, float y) => IsOpen && x >= _left && x < _left + _width && y >= _top && y < _top + _height;

	public void Render(IPainter painter, float windowWidth, float windowHeight, float scale) {
		if (!IsOpen) return;

		IReadOnlyList<ProcessUsage> rows = tree.Rows;
		ITextFont font = painter.GetUi();
		ITextFont header = painter.GetUiStrong();

		float row = RowHeight * scale;
		float padding = Padding * scale;
		float footer = row;
		float maxRows = Math.Max(1f, MathF.Floor((windowHeight * 0.7f - (HeaderHeight * scale) - footer) / row));
		int shown = (int)Math.Min(rows.Count, maxRows);

		_width = Math.Min(Width * scale, windowWidth - (padding * 2f));
		_height = (HeaderHeight * scale) + (shown * row) + footer + padding;
		_left = (windowWidth - _width) / 2f;
		_top = Metrics.Space16 * scale * 3f;

		float radius = Metrics.RadiusLarge * scale;

		painter.PushRoundedClip(_left, _top, _width, _height, radius);
		painter.FillRoundedRectangle(_left, _top, _width, _height, radius, Theme.WidgetBackground);

		float cpuRight = _left + padding + (CpuColumn * scale);
		float memoryRight = cpuRight + (MemoryColumn * scale);
		float pidRight = memoryRight + (PidColumn * scale);
		float nameLeft = pidRight + padding;

		float headerBaseline = _top + ((HeaderHeight * scale) - header.GetLineHeight()) / 2f + header.GetBaseline();
		DrawRight(painter, header, "CPU (%)", cpuRight, headerBaseline, Theme.SectionHeaderForeground);
		DrawRight(painter, header, "Memory (MB)", memoryRight, headerBaseline, Theme.SectionHeaderForeground);
		DrawRight(painter, header, "PID", pidRight, headerBaseline, Theme.SectionHeaderForeground);
		painter.DrawText(header, "Name", nameLeft, headerBaseline, Theme.SectionHeaderForeground);
		painter.DrawHorizontalLine(_top + (HeaderHeight * scale), _left, _left + _width, Theme.WidgetBorder);

		Span<char> buffer = stackalloc char[32];
		float cursor = _top + (HeaderHeight * scale);

		for (int i = 0; i < shown; i++) {
			ProcessUsage usage = rows[i];
			float baseline = cursor + ((row - font.GetLineHeight()) / 2f) + font.GetBaseline();
			Color color = i == 0 ? Theme.Foreground : Theme.SidebarFileForeground;

			usage.CpuPercent.TryFormat(buffer, out int written, "F1");
			DrawRight(painter, font, buffer[..written], cpuRight, baseline, color);

			(usage.MemoryBytes / 1048576.0).TryFormat(buffer, out written, "F0");
			DrawRight(painter, font, buffer[..written], memoryRight, baseline, color);

			usage.Id.TryFormat(buffer, out written);
			DrawRight(painter, font, buffer[..written], pidRight, baseline, Theme.DescriptionForeground);

			painter.DrawText(font, usage.Name, nameLeft + (usage.Depth * Metrics.Space16 * scale), baseline, color);
			cursor += row;
		}

		GCMemoryInfo gc = GC.GetGCMemoryInfo();
		long forge = rows.Count > 0 ? rows[0].MemoryBytes : 0;
		long managed = gc.HeapSizeBytes;

		string summary = $"Forge: managed heap {managed / 1048576.0:F0} MB · native and other {Math.Max(forge - managed, 0) / 1048576.0:F0} MB · {rows.Count} processes";
		painter.DrawText(font, summary, _left + padding, cursor + ((footer - font.GetLineHeight()) / 2f) + font.GetBaseline() + (padding / 2f), Theme.DescriptionForeground);

		painter.PopRoundedClip();
		painter.StrokeRoundedRectangle(_left, _top, _width, _height, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}

	private static void DrawRight(IPainter painter, ITextFont font, ReadOnlySpan<char> text, float right, float baseline, Color color) => painter.DrawText(font, text, right - font.Measure(text), baseline, color);
}
