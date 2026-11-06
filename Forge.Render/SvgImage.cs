using System.Globalization;
using System.Numerics;
using System.Xml;

using D2D = Vortice.Direct2D1;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class SvgImage : IImage, IDisposable
{
	private const float DefaultSize = 300f;
	private const float DefaultHeight = 150f;

	private D2D.ID2D1SvgDocument? _document;

	private SvgImage(D2D.ID2D1SvgDocument document, float width, float height) {
		_document = document;
		Width = width;
		Height = height;
	}

	public float Width { get; }

	public float Height { get; }

	public int GetWidth() => (int)MathF.Ceiling(Width);

	public int GetHeight() => (int)MathF.Ceiling(Height);

	public static SvgImage? Load(RenderDevice device, string filePath) {
		try {
			byte[] bytes = File.ReadAllBytes(filePath);
			(float width, float height) = MeasureDocument(bytes);

			if (device.Context.QueryInterfaceOrNull<D2D.ID2D1DeviceContext5>() is not { } context) return null;

			using (context) {
				using Vortice.WIC.IWICImagingFactory factory = new();
				using Vortice.WIC.IWICStream stream = factory.CreateStream(bytes);

				D2D.ID2D1SvgDocument document = context.CreateSvgDocument(stream, new Vortice.Mathematics.Size(width, height));
				return new SvgImage(document, width, height);
			}
		}
		catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or IOException or UnauthorizedAccessException or XmlException or System.Runtime.InteropServices.COMException) {
			return null;
		}
	}

	private static (float Width, float Height) MeasureDocument(byte[] bytes) {
		using MemoryStream source = new(bytes, writable: false);
		using XmlReader reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });

		while (reader.Read()) {
			if (reader.NodeType != XmlNodeType.Element) continue;

			float? width = Length(reader.GetAttribute("width"));
			float? height = Length(reader.GetAttribute("height"));

			if (width is null || height is null) {
				if (ViewBox(reader.GetAttribute("viewBox")) is { } box && box.Width > 0f && box.Height > 0f) {
					if (width is null && height is null) return (box.Width, box.Height);

					if (width is { } knownWidth) return (knownWidth, knownWidth * box.Height / box.Width);

					return (height!.Value * box.Width / box.Height, height.Value);
				}
			}

			return (width ?? DefaultSize, height ?? DefaultHeight);
		}

		return (DefaultSize, DefaultHeight);
	}

	private static float? Length(string? value) {
		if (string.IsNullOrWhiteSpace(value)) return null;

		ReadOnlySpan<char> text = value.AsSpan().Trim();

		if (text.EndsWith("%")) return null;

		if (text.EndsWith("px")) text = text[..^2];

		return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) && parsed > 0f ? parsed : null;
	}

	private static (float Width, float Height)? ViewBox(string? value) {
		if (string.IsNullOrWhiteSpace(value)) return null;

		Span<Range> parts = stackalloc Range[5];
		ReadOnlySpan<char> text = value;
		int count = text.SplitAny(parts, [' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);

		if (count != 4) return null;

		return float.TryParse(text[parts[2]], NumberStyles.Float, CultureInfo.InvariantCulture, out float width) && float.TryParse(text[parts[3]], NumberStyles.Float, CultureInfo.InvariantCulture, out float height) ? (width, height) : null;
	}

	internal void Draw(D2D.ID2D1DeviceContext context, float x, float y, float width, float height) {
		if (_document is null) return;

		using D2D.ID2D1DeviceContext5? svg = context.QueryInterfaceOrNull<D2D.ID2D1DeviceContext5>();

		if (svg is null) return;

		Matrix3x2 previous = context.Transform;
		context.Transform = Matrix3x2.CreateScale(width / Width, height / Height) * Matrix3x2.CreateTranslation(x, y) * previous;
		svg.DrawSvgDocument(_document);
		context.Transform = previous;
	}

	public void Dispose() {
		_document?.Dispose();
		_document = null;
	}
}
