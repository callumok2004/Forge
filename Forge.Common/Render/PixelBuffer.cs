namespace Forge.Common.Render;

public sealed class PixelBuffer
{
	public uint[] Pixels { get; private set; } = [];

	public int Width { get; private set; }

	public int Height { get; private set; }

	public int Version { get; private set; }

	public object? NativeCache { get; set; }

	public int NativeVersion { get; set; } = -1;

	public void Resize(int width, int height) {
		width = Math.Max(1, width);
		height = Math.Max(1, height);

		if (width == Width && height == Height) return;

		Width = width;
		Height = height;

		if (Pixels.Length < width * height) Pixels = new uint[width * height];

		Version++;
	}

	public Span<uint> Span => Pixels.AsSpan(0, Width * Height);

	public void Commit() => Version++;
}
