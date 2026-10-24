using System.Diagnostics;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.Shell;

public sealed class ProcessExplorer : Panel
{
	private const float PreferredWidth = 640f;
	private const float RowHeight = 22f;
	private const float HeaderHeight = 30f;
	private const float Padding = 12f;
	private const float CpuColumn = 80f;
	private const float MemoryColumn = 110f;
	private const float PidColumn = 80f;
	private const int MaxRows = 24;

	private readonly ProcessTree _tree;

	private IReadOnlyList<ProcessUsage> _rows = [];
	private int _scroll;
	private int _hover = -1;
	private uint _selected;

	public ProcessExplorer(ProcessTree tree) {
		_tree = tree;
		KeyboardInputEnabled = true;
		ContextKey = "processExplorerFocus";
	}

	public bool IsOpen => IsPopup;

	public void Toggle() {
		if (IsPopup) {
			ClosePopup();
			return;
		}

		Refresh();
		MakePopup(modal: true);
	}

	public void Refresh() {
		_rows = _tree.Rows;
		_scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - MaxRows));

		if (IsPopup) InvalidateLayout();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float wide = PreferredWidth * scale;
		float tall = (HeaderHeight * scale) + (Math.Min(_rows.Count, MaxRows) * RowHeight * scale) + (RowHeight * scale) + (Padding * scale);

		if (Ui is { } ui) SetBounds(MathF.Max((ui.GetViewportWidth() - wide) / 2f, 0f), Metrics.Space16 * 3f * scale, wide, tall);
	}

	private int RowAt(float y) {
		float scale = Scale;
		float top = HeaderHeight * scale;

		if (y < top) return -1;

		int row = _scroll + (int)((y - top) / (RowHeight * scale));
		return row < _rows.Count && row < _scroll + MaxRows ? row : -1;
	}

	public override void OnCursorMoved(float x, float y) {
		int row = RowAt(y);

		if (row == _hover) return;

		_hover = row;
		Repaint();
	}

	public override void OnCursorExited() {
		if (_hover < 0) return;

		_hover = -1;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (RowAt(y) is var row && row >= 0) {
			_selected = _rows[row].Id;
			Repaint();
		}

		return true;
	}

	public override bool OnMouseWheeled(int delta, float x, float y) {
		int previous = _scroll;
		_scroll = Math.Clamp(_scroll - (delta / 120 * 3), 0, Math.Max(0, _rows.Count - MaxRows));

		if (_scroll != previous) Repaint();

		return true;
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key != VirtualKey.Delete || _selected == 0 || _selected == (uint)Environment.ProcessId) return false;

		try {
			using Process process = Process.GetProcessById((int)_selected);
			process.Kill(entireProcessTree: true);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
		}

		_selected = 0;
		return true;
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		ITextFont font = painter.GetUi();
		ITextFont header = painter.GetUiStrong();

		float row = RowHeight * scale;
		float padding = Padding * scale;
		float radius = Metrics.RadiusLarge * scale;
		float left = ScreenX;
		float top = ScreenY;

		painter.PushRoundedClip(left, top, Wide, Tall, radius);
		painter.FillRoundedRectangle(left, top, Wide, Tall, radius, Theme.WidgetBackground);

		float cpuRight = left + padding + (CpuColumn * scale);
		float memoryRight = cpuRight + (MemoryColumn * scale);
		float pidRight = memoryRight + (PidColumn * scale);
		float nameLeft = pidRight + padding;

		float headerBaseline = top + (((HeaderHeight * scale) - header.GetLineHeight()) / 2f) + header.GetBaseline();
		DrawRight(painter, header, "CPU (%)", cpuRight, headerBaseline, Theme.SectionHeaderForeground);
		DrawRight(painter, header, "Memory (MB)", memoryRight, headerBaseline, Theme.SectionHeaderForeground);
		DrawRight(painter, header, "PID", pidRight, headerBaseline, Theme.SectionHeaderForeground);
		painter.DrawText(header, "Name", nameLeft, headerBaseline, Theme.SectionHeaderForeground);
		painter.DrawHorizontalLine(top + (HeaderHeight * scale), left, left + Wide, Theme.WidgetBorder);

		Span<char> buffer = stackalloc char[32];
		float cursor = top + (HeaderHeight * scale);
		int last = Math.Min(_rows.Count, _scroll + MaxRows);

		for (int i = _scroll; i < last; i++) {
			ProcessUsage usage = _rows[i];
			float inset = Metrics.Space4 * scale;

			if (usage.Id == _selected) painter.FillRoundedRectangle(left + inset, cursor, Wide - (inset * 2f), row, Metrics.RadiusSmall * scale, Theme.ListActiveSelectionBackground);
			else if (i == _hover) painter.FillRoundedRectangle(left + inset, cursor, Wide - (inset * 2f), row, Metrics.RadiusSmall * scale, Theme.ListHoverBackground);

			float baseline = cursor + ((row - font.GetLineHeight()) / 2f) + font.GetBaseline();
			Color color = usage.Depth == 0 ? Theme.Foreground : Theme.SidebarFileForeground;

			usage.CpuPercent.TryFormat(buffer, out int written, "F1");
			DrawRight(painter, font, buffer[..written], cpuRight, baseline, color);

			(usage.MemoryBytes / 1048576.0).TryFormat(buffer, out written, "F0");
			DrawRight(painter, font, buffer[..written], memoryRight, baseline, color);

			usage.Id.TryFormat(buffer, out written);
			DrawRight(painter, font, buffer[..written], pidRight, baseline, Theme.DescriptionForeground);

			painter.DrawText(font, usage.Name, nameLeft + (usage.Depth * Metrics.Space16 * scale), baseline, color);
			cursor += row;
		}

		long forge = _rows.Count > 0 ? _rows[0].MemoryBytes : 0;
		long managed = GC.GetGCMemoryInfo().HeapSizeBytes;

		string summary = $"Forge: managed heap {managed / 1048576.0:F0} MB · native and other {Math.Max(forge - managed, 0) / 1048576.0:F0} MB · {_rows.Count} processes · Del kills the selected process";
		painter.DrawText(font, summary, left + padding, cursor + ((row - font.GetLineHeight()) / 2f) + font.GetBaseline() + (padding / 2f), Theme.DescriptionForeground);

		painter.PopRoundedClip();
		painter.StrokeRoundedRectangle(left, top, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.WidgetBorder);
	}

	private static void DrawRight(IPainter painter, ITextFont font, ReadOnlySpan<char> text, float right, float baseline, Color color) => painter.DrawText(font, text, right - font.Measure(text), baseline, color);
}
