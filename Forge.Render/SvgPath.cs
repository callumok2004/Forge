using D2D = Vortice.Direct2D1;
using System.Globalization;
using System.Numerics;

namespace Forge.Render;

internal static class SvgPath
{
	public static void Parse(ReadOnlySpan<char> data, D2D.ID2D1GeometrySink sink) {
		bool figureOpen = false;

		try {
			Run(data, sink, ref figureOpen);
		}
		finally {
			if (figureOpen) sink.EndFigure(D2D.FigureEnd.Open);
		}
	}

	private static void Run(ReadOnlySpan<char> data, D2D.ID2D1GeometrySink sink, ref bool figureOpen) {
		int position = 0;

		Vector2 current = Vector2.Zero;
		Vector2 start = Vector2.Zero;

		Vector2 lastControl = Vector2.Zero;
		char previous = ' ';

		char command = ' ';

		while (true) {
			SkipSeparators(data, ref position);
			if (position >= data.Length) break;

			char character = data[position];

			if (char.IsAsciiLetter(character)) {
				command = character;
				position++;
			}
			else if (command == ' ') break;
			else if (command is 'M' or 'm') command = command == 'M' ? 'L' : 'l';

			bool relative = char.IsLower(command);
			char kind = char.ToUpperInvariant(command);

			switch (kind) {
				case 'M': {
						if (!TryPoint(data, ref position, relative, current, out Vector2 point)) return;

						if (figureOpen) sink.EndFigure(D2D.FigureEnd.Open);

						sink.BeginFigure(point, D2D.FigureBegin.Filled);
						figureOpen = true;

						current = point;
						start = point;
						break;
					}

				case 'L': {
						if (!figureOpen || !TryPoint(data, ref position, relative, current, out Vector2 point)) return;

						sink.AddLine(point);
						current = point;
						break;
					}

				case 'H':
				case 'V': {
						if (!figureOpen || !TryNumber(data, ref position, out float value)) return;

						Vector2 point = kind == 'H'
							? new Vector2(relative ? current.X + value : value, current.Y)
							: new Vector2(current.X, relative ? current.Y + value : value);

						sink.AddLine(point);
						current = point;
						break;
					}

				case 'C':
				case 'S': {
						Vector2 control1;

						if (kind == 'S') {
							control1 = previous is 'C' or 'c' or 'S' or 's'
								? (current * 2f) - lastControl
								: current;
						}
						else if (!TryPoint(data, ref position, relative, current, out control1)) return;

						if (!figureOpen ||
							!TryPoint(data, ref position, relative, current, out Vector2 control2) ||
							!TryPoint(data, ref position, relative, current, out Vector2 end)) return;

						sink.AddBezier(new D2D.BezierSegment(control1, control2, end));

						lastControl = control2;
						current = end;
						break;
					}

				case 'Q':
				case 'T': {
						Vector2 control;

						if (kind == 'T') {
							control = previous is 'Q' or 'q' or 'T' or 't'
								? (current * 2f) - lastControl
								: current;
						}
						else if (!TryPoint(data, ref position, relative, current, out control)) return;

						if (!figureOpen || !TryPoint(data, ref position, relative, current, out Vector2 end)) return;

						sink.AddQuadraticBezier(new D2D.QuadraticBezierSegment {
							Point1 = control,
							Point2 = end,
						});

						lastControl = control;
						current = end;
						break;
					}

				case 'A': {
						if (!figureOpen ||
							!TryNumber(data, ref position, out float radiusX) ||
							!TryNumber(data, ref position, out float radiusY) ||
							!TryNumber(data, ref position, out float rotation) ||
							!TryFlag(data, ref position, out bool largeArc) ||
							!TryFlag(data, ref position, out bool sweep) ||
							!TryPoint(data, ref position, relative, current, out Vector2 end)) return;

						sink.AddArc(new D2D.ArcSegment {
							Point = end,
							Size = new Vortice.Mathematics.Size(MathF.Abs(radiusX), MathF.Abs(radiusY)),
							RotationAngle = rotation,
							SweepDirection = sweep
								? D2D.SweepDirection.Clockwise
								: D2D.SweepDirection.CounterClockwise,
							ArcSize = largeArc ? D2D.ArcSize.Large : D2D.ArcSize.Small,
						});

						current = end;
						break;
					}

				case 'Z': {
						if (figureOpen) {
							sink.EndFigure(D2D.FigureEnd.Closed);
							figureOpen = false;
						}

						current = start;
						break;
					}

				default:
					return;
			}

			previous = command;
		}

	}

	private static bool TryPoint(ReadOnlySpan<char> data, ref int position, bool relative, Vector2 origin,
		out Vector2 point) {
		point = default;

		if (!TryNumber(data, ref position, out float x) || !TryNumber(data, ref position, out float y)) {
			return false;
		}

		point = relative ? new Vector2(origin.X + x, origin.Y + y) : new Vector2(x, y);
		return true;
	}

	private static bool TryFlag(ReadOnlySpan<char> data, ref int position, out bool flag) {
		SkipSeparators(data, ref position);

		flag = false;
		if (position >= data.Length || (data[position] != '0' && data[position] != '1')) return false;

		flag = data[position] == '1';
		position++;
		return true;
	}

	private static bool TryNumber(ReadOnlySpan<char> data, ref int position, out float value) {
		SkipSeparators(data, ref position);

		value = 0f;
		int start = position;

		if (position < data.Length && (data[position] == '-' || data[position] == '+')) position++;

		bool seenPoint = false;

		while (position < data.Length) {
			char digit = data[position];

			if (char.IsAsciiDigit(digit)) {
				position++;
				continue;
			}

			if (digit == '.' && !seenPoint) {
				seenPoint = true;
				position++;
				continue;
			}

			break;
		}

		if (position < data.Length && (data[position] == 'e' || data[position] == 'E')) {
			position++;

			if (position < data.Length && (data[position] == '-' || data[position] == '+')) position++;

			while (position < data.Length && char.IsAsciiDigit(data[position])) position++;
		}

		return position > start &&
			float.TryParse(data[start..position], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	private static void SkipSeparators(ReadOnlySpan<char> data, ref int position) {
		while (position < data.Length && (data[position] is ' ' or ',' or '\t' or '\r' or '\n')) position++;
	}
}
