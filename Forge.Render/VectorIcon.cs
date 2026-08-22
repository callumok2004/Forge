using D2D = Vortice.Direct2D1;
using System.Globalization;
using System.Numerics;

using Forge.Common.Render;
using Forge.Theming;

namespace Forge.Render;

public sealed class VectorIcon : IVectorIcon, IDisposable
{
	private readonly Shape[] _shapes;
	private readonly float _viewBox;

	private readonly float _minX;
	private readonly float _minY;

	private bool _disposed;

	private readonly struct Shape(D2D.ID2D1Geometry geometry, Color fill)
	{
		public readonly D2D.ID2D1Geometry Geometry = geometry;
		public readonly Color Fill = fill;
	}

	private readonly struct Inherited(Color? rootFill, Color gradientFallback)
	{
		public readonly Color? RootFill = rootFill;
		public readonly Color GradientFallback = gradientFallback;
	}

	private VectorIcon(Shape[] shapes, float viewBox, float minX, float minY) {
		_shapes = shapes;
		_viewBox = viewBox;
		_minX = minX;
		_minY = minY;
		Dominant = LargestFill(shapes);
	}

	public Color Dominant { get; }

	public Color GetDominantColor() => Dominant;

	private static Color LargestFill(Shape[] shapes) {
		Color dominant = new(0f, 0f, 0f, 0f);
		float best = 0f;

		foreach (Shape shape in shapes) {
			if (shape.Fill.A < 0.05f) continue;

			float area;

			try {
				area = MathF.Abs(shape.Geometry.ComputeArea()) * shape.Fill.A;
			}
			catch (SharpGen.Runtime.SharpGenException) {
				continue;
			}

			float saturation = MathF.Max(shape.Fill.R, MathF.Max(shape.Fill.G, shape.Fill.B))
				- MathF.Min(shape.Fill.R, MathF.Min(shape.Fill.G, shape.Fill.B));

			area *= 0.35f + saturation;

			if (area <= best) continue;

			best = area;
			dominant = shape.Fill;
		}

		return dominant;
	}

	public static VectorIcon? Load(D2D.ID2D1Factory1 factory, string filePath) {
		string svg;

		try {
			svg = File.ReadAllText(filePath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return null;
		}

		ReadOnlySpan<char> source = svg;
		float viewBox = ReadViewBox(source, out float minX, out float minY);

		List<Shape> shapes = [];

		Color gradientFallback = FirstStopColour(source);

		Inherited inherited = new(RootFill(source), gradientFallback);

		CollectPaths(factory, source, shapes, inherited);
		CollectCircles(factory, source, shapes, inherited);
		CollectRectangles(factory, source, shapes, inherited);

		return shapes.Count > 0 ? new VectorIcon([.. shapes], viewBox, minX, minY) : null;
	}

	internal void Draw(D2D.ID2D1DeviceContext context, D2D.ID2D1SolidColorBrush brush, float centreX,
		float centreY, float size, Color? tint) {
		float factor = size / _viewBox;
		float left = centreX - (size / 2f);
		float top = centreY - (size / 2f);

		Matrix3x2 previous = context.Transform;
		context.Transform =
			Matrix3x2.CreateTranslation(-_minX, -_minY) *
			Matrix3x2.CreateScale(factor) *
			Matrix3x2.CreateTranslation(left, top) *
			previous;

		for (int i = 0; i < _shapes.Length; i++) {
			Color fill = tint ?? _shapes[i].Fill;
			brush.Color = new Vortice.Mathematics.Color4(fill.R, fill.G, fill.B, fill.A);

			context.FillGeometry(_shapes[i].Geometry, brush);
		}

		context.Transform = previous;
	}

	private static void CollectPaths(D2D.ID2D1Factory1 factory, ReadOnlySpan<char> svg, List<Shape> shapes,
		Inherited inherited) {
		int position = 0;

		while (TryFindElement(svg, "<path", ref position, out ReadOnlySpan<char> element)) {
			ReadOnlySpan<char> data = Attribute(element, "d");
			if (data.IsEmpty) continue;

			D2D.ID2D1PathGeometry geometry = factory.CreatePathGeometry();

			try {
				using D2D.ID2D1GeometrySink sink = geometry.Open();

				sink.SetFillMode(D2D.FillMode.Alternate);
				SvgPath.Parse(data, sink);
				sink.Close();
			}
			catch (Exception) {
				geometry.Dispose();
				continue;
			}

			shapes.Add(new Shape(geometry, Fill(element, inherited)));
		}
	}

	private static void CollectCircles(D2D.ID2D1Factory1 factory, ReadOnlySpan<char> svg, List<Shape> shapes,
		Inherited inherited) {
		int position = 0;

		while (TryFindElement(svg, "<circle", ref position, out ReadOnlySpan<char> element)) {
			float radius = Number(element, "r");
			if (radius <= 0f) continue;

			D2D.ID2D1EllipseGeometry geometry = factory.CreateEllipseGeometry(
				new D2D.Ellipse(new Vector2(Number(element, "cx"), Number(element, "cy")), radius, radius));

			shapes.Add(new Shape(geometry, Fill(element, inherited)));
		}
	}

	private static void CollectRectangles(D2D.ID2D1Factory1 factory, ReadOnlySpan<char> svg, List<Shape> shapes,
		Inherited inherited) {
		int position = 0;

		while (TryFindElement(svg, "<rect", ref position, out ReadOnlySpan<char> element)) {
			float width = Number(element, "width");
			float height = Number(element, "height");

			if (width <= 0f || height <= 0f) continue;

			float x = Number(element, "x");
			float y = Number(element, "y");

			shapes.Add(new Shape(
				factory.CreateRectangleGeometry(new Vortice.Mathematics.Rect(x, y, width, height)),
				Fill(element, inherited)));
		}
	}

	private static bool TryFindElement(ReadOnlySpan<char> svg, ReadOnlySpan<char> tag, ref int position,
		out ReadOnlySpan<char> element) {
		element = default;

		if (position >= svg.Length) return false;

		int found = svg[position..].IndexOf(tag, StringComparison.OrdinalIgnoreCase);
		if (found < 0) {
			position = svg.Length;
			return false;
		}

		int start = position + found;
		int end = svg[start..].IndexOf('>');

		if (end < 0) {
			position = svg.Length;
			return false;
		}

		element = svg.Slice(start, end);
		position = start + end;
		return true;
	}

	private static Color Fill(ReadOnlySpan<char> element, Inherited inherited) {
		ReadOnlySpan<char> fill = Attribute(element, "fill");

		if (fill.IsEmpty) return inherited.RootFill ?? Theme.EditorForeground;

		return Resolve(fill, inherited.GradientFallback) ?? Theme.EditorForeground;
	}

	private static Color? RootFill(ReadOnlySpan<char> svg) {
		int position = 0;

		if (!TryFindElement(svg, "<svg", ref position, out ReadOnlySpan<char> root)) return null;

		ReadOnlySpan<char> fill = Attribute(root, "fill");
		return fill.IsEmpty ? null : Resolve(fill, Theme.EditorForeground);
	}

	private static Color? Resolve(ReadOnlySpan<char> fill, Color gradientFallback) {
		if (fill.Equals("none", StringComparison.OrdinalIgnoreCase) ||
			fill.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return new Color(0f, 0f, 0f, 0f);

		if (fill.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return gradientFallback;

		return fill[0] == '#' ? Color.Parse(fill) : null;
	}

	private static Color FirstStopColour(ReadOnlySpan<char> svg) {
		int position = 0;

		if (TryFindElement(svg, "<stop", ref position, out ReadOnlySpan<char> stop)) {
			ReadOnlySpan<char> colour = Attribute(stop, "stop-color");
			if (colour.Length > 0 && colour[0] == '#') return Color.Parse(colour);
		}

		return Theme.EditorForeground;
	}

	private static float ReadViewBox(ReadOnlySpan<char> svg, out float minX, out float minY) {
		minX = 0f;
		minY = 0f;

		int position = 0;

		if (!TryFindElement(svg, "<svg", ref position, out ReadOnlySpan<char> element)) return 24f;

		ReadOnlySpan<char> viewBox = Attribute(element, "viewBox");

		float extent = 24f;
		int field = 0;

		foreach (Range range in viewBox.Split(' ')) {
			ReadOnlySpan<char> text = viewBox[range];

			if (text.IsEmpty) continue;

			if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) break;

			switch (field++) {
				case 0:
					minX = value;
					break;

				case 1:
					minY = value;
					break;

				case 2:
					if (value > 0f) extent = value;

					return extent;
			}
		}

		return extent;
	}

	private static float Number(ReadOnlySpan<char> element, ReadOnlySpan<char> name) =>
		float.TryParse(Attribute(element, name), NumberStyles.Float, CultureInfo.InvariantCulture,
			out float value)
			? value
			: 0f;

	private static ReadOnlySpan<char> Attribute(ReadOnlySpan<char> element, ReadOnlySpan<char> name) {
		Span<char> pattern = stackalloc char[name.Length + 2];
		name.CopyTo(pattern);
		pattern[name.Length] = '=';
		pattern[name.Length + 1] = '"';

		int offset = 0;

		while (true) {
			int found = element[offset..].IndexOf(pattern, StringComparison.Ordinal);
			if (found < 0) return default;

			found += offset;

			if (found > 0 && !char.IsWhiteSpace(element[found - 1])) {
				offset = found + pattern.Length;
				continue;
			}

			ReadOnlySpan<char> rest = element[(found + pattern.Length)..];
			int end = rest.IndexOf('"');

			return end < 0 ? default : rest[..end];
		}
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		for (int i = 0; i < _shapes.Length; i++) _shapes[i].Geometry.Dispose();
	}
}
