using System.Numerics;

using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;

namespace Forge.Extensions.DotnetProfiler.Views;

public enum ChartVariant : byte
{
	Card,
	Mini,
	Panel,
}

public sealed class Sparkline : Panel
{
	private const float AxisLerp = 0.12f;
	private const float AxisEpsilon = 0.001f;
	private const int Subdivisions = 6;

	private static readonly Color[] GenerationColors = [Color.Parse("#4ec9b0"), Color.Parse("#dcb45a"), Color.Parse("#e06c75")];

	private readonly CounterHistory _history;
	private readonly string _label;
	private readonly Func<double, string> _format;
	private readonly bool _markers;
	private readonly Color _color;
	private readonly ChartVariant _variant;

	private Vector2[] _points = new Vector2[CounterHistory.Capacity * Subdivisions + 4];
	private double _top;
	private double _bottom;
	private bool _initialised;
	private int _drawnVersion = -1;
	private double _range = 1;
	private float _chartBottom;
	private float _chartHeight;

	public Sparkline(CounterHistory history, string label, Color color, Func<double, string> format, bool markers, ChartVariant variant) {
		_history = history;
		_label = label.ToUpperInvariant();
		_color = color;
		_format = format;
		_markers = markers;
		_variant = variant;
		Name = label;
		MouseInputEnabled = false;
	}

	private readonly record struct Style(float PadX, float PadY, float Radius, float LabelSize, float ReadoutSize, float BoundsSize, float BoundsGap, float HeaderGap, float Canvas);

	private static Style StyleOf(ChartVariant variant) => variant switch {
		ChartVariant.Card => new Style(10f, 8f, 5f, 11f, 13f, 9.5f, 8f, 6f, 56f),
		ChartVariant.Mini => new Style(8f, 6f, 4f, 10f, 12f, 9f, 6f, 4f, 44f),
		_ => new Style(8f, 6f, 4f, 10f, 12f, 9f, 6f, 4f, 60f),
	};

	public static float PreferredHeight(float scale, ChartVariant variant) {
		Style style = StyleOf(variant);

		return ((style.PadY * 2f) + (style.ReadoutSize * 1.35f) + style.HeaderGap + style.Canvas + 2f) * scale;
	}

	public void Sync() {
		if (_history.Version == _drawnVersion) return;

		Repaint();
	}

	public override void OnThink(float deltaSeconds) => Repaint();

	public override void Paint(IPainter painter) {
		float scale = Scale;
		Style style = StyleOf(_variant);
		ITextFont labelFont = painter.GetUi(style.LabelSize, strong: true);
		ITextFont readoutFont = painter.GetUi(style.ReadoutSize, strong: true);
		ITextFont boundsFont = painter.GetUi(style.BoundsSize);
		float radius = style.Radius * scale;
		float left = ScreenX + (style.PadX * scale);
		float right = ScreenX + Wide - (style.PadX * scale);
		float top = ScreenY + (style.PadY * scale);
		float header = readoutFont.GetLineHeight();
		float baseline = top + readoutFont.GetBaseline();

		_drawnVersion = _history.Version;

		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Theme.EditorForeground.WithAlpha(0.04f));
		painter.StrokeRoundedRectangle(ScreenX, ScreenY, Wide, Tall, radius, Metrics.StrokeThickness * scale, Theme.PanelBorder);

		string readout = double.IsNaN(_history.Latest) ? "–" : _format(_history.Latest);
		float readoutWidth = readoutFont.Measure(readout);
		painter.DrawText(readoutFont, readout, right - readoutWidth, baseline, Theme.EditorForeground);
		painter.DrawText(labelFont, _label, left, baseline, Theme.EditorForeground.WithAlpha(0.75f));

		float chartTop = top + header + (style.HeaderGap * scale);
		float chartHeight = MathF.Max(0f, ScreenY + Tall - (style.PadY * scale) - chartTop);
		float chartWidth = right - left;

		painter.FillRectangle(left, chartTop, chartWidth, chartHeight, Theme.EditorBackground);
		painter.FillRectangle(left, chartTop, chartWidth, chartHeight, Theme.EditorForeground.WithAlpha(0.06f));

		int count = _history.Count;

		if (count < 2 || chartHeight <= 0f) {
			SetThinking(false);
			return;
		}

		double max = double.NegativeInfinity;
		double min = double.PositiveInfinity;

		for (int i = 0; i < count; i++) {
			double value = _history.At(i);
			max = Math.Max(max, value);
			min = Math.Min(min, value);
		}

		string bounds = max == min ? _format(max) : $"{_format(min)} – {_format(max)}";
		float boundsLeft = left + labelFont.Measure(_label) + (6f * scale);
		float boundsRight = right - readoutWidth - (style.BoundsGap * scale);
		float boundsWidth = boundsFont.Measure(bounds);

		if (boundsRight > boundsLeft) {
			painter.PushClip(boundsLeft, top, boundsRight - boundsLeft, header);
			painter.DrawText(boundsFont, bounds, MathF.Max(boundsLeft, boundsRight - boundsWidth), baseline, Theme.EditorForeground.WithAlpha(0.5f));
			painter.PopClip();
		}

		double span = max - min;
		double headroom = span > 0 ? span * 0.18 : Math.Abs(max) * 0.18 is var fallback && fallback > 0 ? fallback : 1;
		double targetTop = max + headroom;
		double targetBottom = Math.Max(min - headroom, min >= 0 ? 0 : min - headroom);

		if (!_initialised) {
			_top = targetTop;
			_bottom = targetBottom;
			_initialised = true;
		}
		else {
			_top += (targetTop - _top) * AxisLerp;
			_bottom += (targetBottom - _bottom) * AxisLerp;
		}

		double magnitude = Math.Max(Math.Abs(targetTop), 1e-9);
		bool settled = Math.Abs(targetTop - _top) / magnitude < AxisEpsilon && Math.Abs(targetBottom - _bottom) / magnitude < AxisEpsilon;

		if (settled) {
			_top = targetTop;
			_bottom = targetBottom;
		}

		SetThinking(!settled);

		_range = _top - _bottom is var r && r != 0 ? r : 1;
		_chartBottom = chartTop + chartHeight;
		_chartHeight = chartHeight;

		painter.PushClip(left, chartTop, chartWidth, chartHeight);

		float step = chartWidth / (CounterHistory.Capacity - 1);
		int written = Curve(count, step, left);
		Span<Vector2> line = _points.AsSpan(0, written);

		_points[written] = new Vector2(line[^1].X, _chartBottom);
		_points[written + 1] = new Vector2(line[0].X, _chartBottom);
		painter.FillPolygon(_points.AsSpan(0, written + 2), _color.WithAlpha(0.18f));
		painter.StrokePolyline(line, 1.5f * scale, _color);

		Dashed(painter, MapY(max), left, right, scale);

		if (max != min) Dashed(painter, MapY(min), left, right, scale);

		if (_markers) DrawMarkers(painter, count, step, left, chartTop, chartHeight, scale);

		painter.PopClip();
	}

	private void Dashed(IPainter painter, float y, float left, float right, float scale) {
		Color color = _color.WithAlpha(0.16f);
		float dash = 2f * scale;
		float gap = 4f * scale;
		float thickness = MathF.Max(1f, scale * 0.5f);

		for (float x = left; x < right; x += dash + gap) painter.FillRectangle(x, y, MathF.Min(dash, right - x), thickness, color);
	}

	private float MapY(double value) => (float)(_chartBottom - ((value - _bottom) / _range * (_chartHeight * 0.82)) - (_chartHeight * 0.09));

	private Vector2 Point(int i, int count, float step, float left) {
		int index = Math.Clamp(i, 0, count - 1);

		return new Vector2(left + (index * step), MapY(_history.At(index)));
	}

	private int Curve(int count, float step, float left) {
		int needed = ((count - 1) * Subdivisions) + 3;

		if (_points.Length < needed) _points = new Vector2[needed];

		Span<Vector2> points = _points;
		int written = 0;

		points[written++] = Point(0, count, step, left);

		for (int i = 0; i < count - 1; i++) {
			Vector2 p0 = Point(i - 1, count, step, left);
			Vector2 p1 = Point(i, count, step, left);
			Vector2 p2 = Point(i + 1, count, step, left);
			Vector2 p3 = Point(i + 2, count, step, left);
			Vector2 c1 = p1 + ((p2 - p0) / 6f);
			Vector2 c2 = p2 - ((p3 - p1) / 6f);

			for (int s = 1; s <= Subdivisions; s++) {
				float t = s / (float)Subdivisions;
				float u = 1f - t;
				points[written++] = (u * u * u * p1) + (3f * u * u * t * c1) + (3f * u * t * t * c2) + (t * t * t * p2);
			}
		}

		return written;
	}

	private void DrawMarkers(IPainter painter, int count, float step, float left, float chartTop, float chartHeight, float scale) {
		float size = 3f * scale;
		float baseline = chartTop + chartHeight - size - scale;

		for (int i = 0; i < count; i++) {
			byte level = _history.MarkAt(i);

			if (level == 0) continue;

			float x = left + (i * step);
			Color color = GenerationColors[level - 1];
			painter.FillTriangle(x, baseline, x - size, baseline + (size * 1.6f), x + size, baseline + (size * 1.6f), color.WithAlpha(0.95f));

			if (level == 3) painter.FillRectangle(x - (scale * 0.5f), chartTop, scale, chartHeight, color.WithAlpha(0.22f));
		}
	}
}
