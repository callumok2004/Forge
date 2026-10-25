using Forge.Common.Render;
using Forge.Common.UI;

namespace Forge.UI.Controls;

public sealed class ImagePanel : Panel
{
	public IImage? Image {
		get;
		set {
			if (ReferenceEquals(field, value)) return;

			field = value;
			Repaint();
		}
	}

	public float ImageSize { get; set; } = 16f;

	public override void Paint(IPainter painter) {
		if (Image is { } image) painter.DrawImage(image, ScreenX + (Wide / 2f), ScreenY + (Tall / 2f), ImageSize * Scale);
	}
}
