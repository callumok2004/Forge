using D2D = Vortice.Direct2D1;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class Image : IImage, IDisposable
{
	private D2D.ID2D1Bitmap _bitmap;
	private bool _disposed;

	public Image(RenderDevice device, string filePath) {
		byte[] pixels = File.ReadAllBytes(filePath);

		int side = (int)Math.Sqrt(pixels.Length / 4);
		if (side <= 0 || side * side * 4 != pixels.Length) {
			throw new InvalidDataException($"'{filePath}' is not a square 32-bit BGRA image.");
		}

		Width = side;
		Height = side;

		D2D.BitmapProperties1 properties = new(
			new Vortice.DCommon.PixelFormat(
				Vortice.DXGI.Format.B8G8R8A8_UNorm,
				Vortice.DCommon.AlphaMode.Premultiplied));

		unsafe {
			fixed (byte* source = pixels) {
				_bitmap = device.Context.CreateBitmap(
					new Vortice.Mathematics.SizeI(side, side),
					(nint)source,
					(uint)(side * 4),
					properties);
			}
		}
	}

	public int Width { get; }

	public int Height { get; }

	public int GetWidth() => Width;

	public int GetHeight() => Height;

	internal D2D.ID2D1Bitmap Bitmap => _bitmap;

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;
		_bitmap?.Dispose();
		_bitmap = null!;
	}
}
