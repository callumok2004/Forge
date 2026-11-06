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
		SourceWidth = side;
		SourceHeight = side;

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

	private Image(D2D.ID2D1Bitmap bitmap, int width, int height, int sourceWidth, int sourceHeight) {
		_bitmap = bitmap;
		Width = width;
		Height = height;
		SourceWidth = sourceWidth;
		SourceHeight = sourceHeight;
	}

	public int SourceWidth { get; }

	public int SourceHeight { get; }

	public static Image? Decode(RenderDevice device, string filePath) {
		try {
			using Vortice.WIC.IWICImagingFactory factory = new();
			using Vortice.WIC.IWICBitmapDecoder decoder = factory.CreateDecoderFromFileName(filePath, FileAccess.Read, Vortice.WIC.DecodeOptions.CacheOnDemand);
			using Vortice.WIC.IWICBitmapFrameDecode frame = decoder.GetFrame(0);
			using Vortice.WIC.IWICFormatConverter converter = factory.CreateFormatConverter();

			converter.Initialize(frame, Vortice.WIC.PixelFormat.Format32bppPBGRA, Vortice.WIC.BitmapDitherType.None, null, 0d, Vortice.WIC.BitmapPaletteType.Custom);

			frame.GetSize(out uint sourceWidth, out uint sourceHeight);

			uint limit = device.Context.MaximumBitmapSize;
			double shrink = Math.Min(1d, Math.Min((double)limit / Math.Max(1u, sourceWidth), (double)limit / Math.Max(1u, sourceHeight)));

			if (shrink >= 1d) {
				D2D.ID2D1Bitmap bitmap = device.Context.CreateBitmapFromWicBitmap(converter, null);
				return new Image(bitmap, (int)sourceWidth, (int)sourceHeight, (int)sourceWidth, (int)sourceHeight);
			}

			uint width = Math.Max(1u, (uint)(sourceWidth * shrink));
			uint height = Math.Max(1u, (uint)(sourceHeight * shrink));

			using Vortice.WIC.IWICBitmapScaler scaler = factory.CreateBitmapScaler();
			scaler.Initialize(converter, width, height, Vortice.WIC.BitmapInterpolationMode.Fant);

			D2D.ID2D1Bitmap scaled = device.Context.CreateBitmapFromWicBitmap(scaler, null);
			return new Image(scaled, (int)width, (int)height, (int)sourceWidth, (int)sourceHeight);
		}
		catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
			return null;
		}
	}

	public int Width { get; }

	public int Height { get; }

	public int GetWidth() => Width;

	public int GetHeight() => Height;

	public int GetSourceWidth() => SourceWidth;

	public int GetSourceHeight() => SourceHeight;

	internal D2D.ID2D1Bitmap Bitmap => _bitmap;

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;
		_bitmap?.Dispose();
		_bitmap = null!;
	}
}
