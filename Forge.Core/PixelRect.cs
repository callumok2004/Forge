namespace Forge.Core;

public readonly struct PixelRect(int left, int top, int right, int bottom)
{
	public int Left { get; } = left;

	public int Top { get; } = top;

	public int Right { get; } = right;

	public int Bottom { get; } = bottom;

	public static PixelRect Empty => default;

	public static PixelRect FromSize(int width, int height) => new(0, 0, width, height);

	public static PixelRect Around(float x, float y, float width, float height) =>
		new((int)MathF.Floor(x), (int)MathF.Floor(y),
			(int)MathF.Ceiling(x + width), (int)MathF.Ceiling(y + height));

	public int Width => Right - Left;

	public int Height => Bottom - Top;

	public bool IsEmpty => Right <= Left || Bottom <= Top;

	public PixelRect Union(PixelRect other) {
		if (IsEmpty) return other;

		if (other.IsEmpty) return this;

		return new(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
			Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
	}

	public PixelRect Intersect(PixelRect other) =>
		new(Math.Max(Left, other.Left), Math.Max(Top, other.Top),
			Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));

	public bool Contains(PixelRect other) =>
		other.IsEmpty || (Left <= other.Left && Top <= other.Top &&
			Right >= other.Right && Bottom >= other.Bottom);

	public bool Intersects(float x, float y, float width, float height) =>
		!IsEmpty && width > 0f && height > 0f &&
		x < Right && x + width > Left && y < Bottom && y + height > Top;
}
