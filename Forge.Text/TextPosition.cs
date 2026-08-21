namespace Forge.Text;

public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
	public int CompareTo(TextPosition other) {
		int byLine = Line.CompareTo(other.Line);
		return byLine != 0 ? byLine : Column.CompareTo(other.Column);
	}

	public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;

	public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;

	public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;

	public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;

	public override string ToString() => $"{Line}:{Column}";
}

public readonly record struct TextSpan(int Start, int Length)
{
	public int End => Start + Length;

	public bool IsEmpty => Length == 0;

	public static TextSpan FromBounds(int start, int end) => new(start, end - start);

	public bool Contains(int offset) => (uint)(offset - Start) < (uint)Length;

	public bool IntersectsWith(TextSpan other) => (other.Start < End) && (Start < other.End);

	public override string ToString() => $"[{Start}..{End})";
}

public enum LineEnding
{
	CrLf,

	Lf,
}

public static class LineEndingExtensions
{
	public static ReadOnlySpan<char> ToSpan(this LineEnding ending) =>
		ending == LineEnding.Lf ? "\n" : "\r\n";

	public static int Length(this LineEnding ending) => ending == LineEnding.Lf ? 1 : 2;
}
