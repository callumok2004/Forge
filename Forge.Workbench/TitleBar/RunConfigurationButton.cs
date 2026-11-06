using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class RunConfigurationButton : Button
{
	private const float DotSize = 7f;
	private const float Gap = 6f;
	private const float LeadingPadding = 8f;

	public RunConfigurationButton() {
		Name = "RunConfiguration";
		Command = "forge.debug.pickConfiguration";
		Foreground = Theme.TitleBarForeground;
		Radius = Metrics.RadiusMedium;
		IconColor = _ => Theme.TitleBarForeground.WithAlpha(0.55f);
		SizeToContents = true;
		ApplyPadding();
	}

	public bool Debugging {
		get;
		set {
			if (field == value) return;

			field = value;
			ApplyPadding();
		}
	}

	public bool Paused {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	private const float UsageFontSize = 11f;
	private const float UsageGap = 10f;
	private const float UsageInnerGap = 8f;
	private const float DividerHeight = 14f;
	private const string UsageTemplate = "100.0%000.0 MB";

	private readonly char[] _cpuText = new char[16];
	private readonly char[] _memoryText = new char[16];
	private int _cpuLength;
	private int _memoryLength;
	private float _usageWidth;

	public ResourceUsage? Usage {
		get;
		set {
			if (Nullable.Equals(field, value)) return;

			bool layout = field.HasValue != value.HasValue;
			field = value;

			if (value is { } usage) Format(usage);

			if (layout) ApplyPadding();
			else Repaint();
		}
	}

	private Color Tint => Paused ? Theme.DebugIconPause : Theme.DebugIconStart;

	private bool ShowsUsage => Debugging && Usage is not null;

	private void Format(ResourceUsage usage) {
		usage.CpuPercent.TryFormat(_cpuText, out _cpuLength, "0.0");
		_cpuText[_cpuLength++] = '%';

		double megabytes = usage.MemoryBytes / (1024.0 * 1024.0);
		bool gigabytes = megabytes >= 1024;
		(gigabytes ? megabytes / 1024 : megabytes).TryFormat(_memoryText, out _memoryLength, gigabytes ? "0.00" : "0");
		" GB".CopyTo(_memoryText.AsSpan(_memoryLength));
		_memoryText[_memoryLength + 1] = gigabytes ? 'G' : 'M';
		_memoryLength += 3;
	}

	private void ApplyPadding() {
		_usageWidth = 0f;

		if (ShowsUsage && Ui is not null) {
			float scale = Scale;
			ITextFont font = Ui.GetFonts().GetUi(UsageFontSize, false);
			_usageWidth = ((UsageGap * 2f) + UsageInnerGap) * scale + font.Measure(UsageTemplate);
		}

		Padding = new Thickness(LeadingPadding + (Debugging ? DotSize + Gap : 0f), 0f, Gap + (_usageWidth / Math.Max(Scale, 0.01f)), 0f);

		if (Ui is not null) FitContents();

		Parent?.InvalidateLayout();
		Repaint();
	}

	public override void PaintBackground(IPainter painter) {
		if (Debugging) painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Radius * Scale, Tint.WithAlpha(0.16f));
		else base.PaintBackground(painter);
	}

	public override void Paint(IPainter painter) {
		if (Debugging) {
			float scale = Scale;
			float dot = DotSize * scale;
			float centreX = ScreenX + (LeadingPadding * scale) + (dot / 2f);
			float centreY = ScreenY + (Tall / 2f);

			painter.FillCircle(centreX, centreY, dot, Tint.WithAlpha(0.25f));
			painter.FillCircle(centreX, centreY, dot / 2f, Tint);
		}

		base.Paint(painter);

		if (!ShowsUsage || Ui is null) return;

		float s = Scale;
		ITextFont font = Ui.GetFonts().GetUi(UsageFontSize, false);
		float left = ScreenX + Wide - (Gap * s) - _usageWidth;
		float middle = ScreenY + (Tall / 2f);
		float divider = DividerHeight * s;
		float baseline = middle - (font.GetLineHeight() / 2f) + font.GetBaseline();
		Color text = Theme.TitleBarForeground.WithAlpha(0.7f);

		painter.FillRectangle(left + (UsageGap * s), middle - (divider / 2f), s, divider, Tint.WithAlpha(0.35f));

		float x = left + (UsageGap * 2f * s);
		ReadOnlySpan<char> cpu = _cpuText.AsSpan(0, _cpuLength);
		ReadOnlySpan<char> memory = _memoryText.AsSpan(0, _memoryLength);
		float cpuSlot = font.Measure("100.0%");

		painter.DrawText(font, cpu, x + cpuSlot - font.Measure(cpu), baseline, text);
		painter.DrawText(font, memory, x + cpuSlot + (UsageInnerGap * s), baseline, text);
	}
}
