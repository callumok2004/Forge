using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.Workbench;

public enum CaptionKind : byte
{
	Minimize,
	Maximize,
	Close,
}

public sealed class CaptionButton : Panel
{
	private const float Width = 46f;
	private const float GlyphSize = 10f;

	private static readonly Color CloseHover = Color.Parse("#e81123e6");
	private static readonly Color Hover = Color.Parse("#ffffff1a");
	private static readonly Color White = Color.Parse("#ffffff");

	public CaptionButton(CaptionKind kind) {
		Kind = kind;
		Name = kind.ToString();
		Dock = DockType.Right;
	}

	public CaptionKind Kind { get; }

	public bool Maximized {
		get;
		set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public nint HitCode => Kind switch {
		CaptionKind.Minimize => Win32.HtMinButton,
		CaptionKind.Maximize => Win32.HtMaxButton,
		_ => Win32.HtClose,
	};

	public static float WidthFor(float scale) => Width * scale;

	public override void OnCursorEntered() => Repaint();

	public override void OnCursorExited() => Repaint();

	public override void PaintBackground(IPainter painter) {
		if (IsCursorOver) painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Kind == CaptionKind.Close ? CloseHover : Hover);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		Color color = IsCursorOver && Kind == CaptionKind.Close ? White : Theme.TitleBarForeground;
		float centreX = ScreenX + (Wide / 2f);
		float centreY = ScreenY + (Tall / 2f);
		float size = GlyphSize * scale;
		float half = size / 2f;
		float inset = 2f * scale;

		switch (Kind) {
			case CaptionKind.Minimize:
				painter.DrawHorizontalLine(centreY, centreX - half, centreX + half, color);
				break;

			case CaptionKind.Maximize when Maximized:
				StrokeRectangle(painter, centreX - half, centreY - half + inset, size - inset, size - inset, color);
				painter.DrawHorizontalLine(centreY - half, centreX - half + inset, centreX + half, color);
				painter.DrawVerticalLine(centreX + half, centreY - half, centreY + half - inset, color);
				break;

			case CaptionKind.Maximize:
				StrokeRectangle(painter, centreX - half, centreY - half, size, size, color);
				break;

			case CaptionKind.Close:
				painter.StrokeLine(centreX - half, centreY - half, centreX + half, centreY + half, 1.2f, color);
				painter.StrokeLine(centreX + half, centreY - half, centreX - half, centreY + half, 1.2f, color);
				break;
		}
	}

	private static void StrokeRectangle(IPainter painter, float x, float y, float width, float height, Color color) {
		painter.DrawHorizontalLine(y, x, x + width, color);
		painter.DrawHorizontalLine(y + height, x, x + width, color);
		painter.DrawVerticalLine(x, y, y + height, color);
		painter.DrawVerticalLine(x + width, y, y + height + 1f, color);
	}
}
