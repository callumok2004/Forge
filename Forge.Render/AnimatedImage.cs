using D2D = Vortice.Direct2D1;
using WIC = Vortice.WIC;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class AnimatedImage : IAnimatedImage, IDisposable
{
	private const int MinimumDelay = 20;
	private const int DefaultDelay = 100;

	private readonly RenderDevice _device;
	private readonly WIC.IWICImagingFactory _factory;
	private readonly WIC.IWICBitmapDecoder _decoder;
	private readonly int _frameCount;
	private D2D.ID2D1Bitmap1? _composite;
	private D2D.ID2D1Bitmap1? _restore;
	private int _frame = -1;
	private int _disposal;
	private Vortice.Mathematics.Rect _previousRect;

	private AnimatedImage(RenderDevice device, WIC.IWICImagingFactory factory, WIC.IWICBitmapDecoder decoder, int width, int height) {
		_device = device;
		_factory = factory;
		_decoder = decoder;
		_frameCount = (int)decoder.FrameCount;
		Width = width;
		Height = height;

		D2D.BitmapProperties1 properties = new(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, D2D.BitmapOptions.Target);
		_composite = device.Context.CreateBitmap(new Vortice.Mathematics.SizeI(width, height), 0, 0, properties);

		Advance();
	}

	public int Width { get; }

	public int Height { get; }

	public int CurrentDelay { get; private set; } = DefaultDelay;

	public int GetWidth() => Width;

	public int GetHeight() => Height;

	internal D2D.ID2D1Bitmap? Bitmap => _composite;

	public static IImage? Decode(RenderDevice device, string filePath) {
		WIC.IWICImagingFactory? factory = null;
		WIC.IWICBitmapDecoder? decoder = null;

		try {
			factory = new WIC.IWICImagingFactory();
			decoder = factory.CreateDecoderFromFileName(filePath, FileAccess.Read, WIC.DecodeOptions.CacheOnDemand);

			if (decoder.FrameCount < 2) {
				decoder.Dispose();
				factory.Dispose();
				return Image.Decode(device, filePath);
			}

			using WIC.IWICMetadataQueryReader metadata = decoder.MetadataQueryReader;
			int width = Read(metadata, "/logscrdesc/Width", 0);
			int height = Read(metadata, "/logscrdesc/Height", 0);

			if (width <= 0 || height <= 0) {
				using WIC.IWICBitmapFrameDecode first = decoder.GetFrame(0);
				first.GetSize(out uint frameWidth, out uint frameHeight);
				width = (int)frameWidth;
				height = (int)frameHeight;
			}

			return new AnimatedImage(device, factory, decoder, width, height);
		}
		catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
			decoder?.Dispose();
			factory?.Dispose();
			return Image.Decode(device, filePath);
		}
	}

	private static int Read(WIC.IWICMetadataQueryReader reader, string name, int fallback) {
		try {
			return Convert.ToInt32(reader.GetMetadataByName(name), System.Globalization.CultureInfo.InvariantCulture);
		}
		catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidCastException or FormatException or OverflowException or System.Runtime.InteropServices.COMException) {
			return fallback;
		}
	}

	public void Advance() {
		if (_composite is null) return;

		int next = (_frame + 1) % _frameCount;
		D2D.ID2D1DeviceContext context = _device.Context;

		try {
			using WIC.IWICBitmapFrameDecode frame = _decoder.GetFrame((uint)next);
			using WIC.IWICMetadataQueryReader metadata = frame.MetadataQueryReader;
			using WIC.IWICFormatConverter converter = _factory.CreateFormatConverter();

			converter.Initialize(frame, WIC.PixelFormat.Format32bppPBGRA, WIC.BitmapDitherType.None, null, 0d, WIC.BitmapPaletteType.Custom);
			frame.GetSize(out uint frameWidth, out uint frameHeight);

			float left = Read(metadata, "/imgdesc/Left", 0);
			float top = Read(metadata, "/imgdesc/Top", 0);
			int disposal = Read(metadata, "/grctlext/Disposal", 0);
			int delay = Read(metadata, "/grctlext/Delay", DefaultDelay / 10) * 10;

			using D2D.ID2D1Bitmap source = context.CreateBitmapFromWicBitmap(converter, null);

			D2D.ID2D1Image previousTarget = context.Target;
			context.Target = _composite;
			context.BeginDraw();

			if (next == 0) context.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
			else if (_disposal == 2) {
				context.PushAxisAlignedClip(_previousRect, D2D.AntialiasMode.Aliased);
				context.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
				context.PopAxisAlignedClip();
			}
			else if (_disposal == 3 && _restore is not null) {
				context.PushAxisAlignedClip(_previousRect, D2D.AntialiasMode.Aliased);
				context.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
				context.DrawBitmap(_restore, 1f, D2D.InterpolationMode.NearestNeighbor);
				context.PopAxisAlignedClip();
			}

			context.EndDraw();

			if (disposal == 3) {
				_restore ??= context.CreateBitmap(new Vortice.Mathematics.SizeI(Width, Height), 0, 0, new D2D.BitmapProperties1(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
				_restore.CopyFromBitmap(_composite);
			}

			context.BeginDraw();
			context.DrawBitmap(source, new Vortice.Mathematics.Rect(left, top, frameWidth, frameHeight), 1f, D2D.BitmapInterpolationMode.NearestNeighbor, null);
			context.EndDraw();
			context.Target = previousTarget;
			previousTarget?.Dispose();

			_previousRect = new Vortice.Mathematics.Rect(left, top, frameWidth, frameHeight);
			_disposal = disposal;
			_frame = next;
			CurrentDelay = Math.Max(MinimumDelay, delay);
		}
		catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException) {
			CurrentDelay = DefaultDelay;
		}
	}

	public void Dispose() {
		_composite?.Dispose();
		_composite = null;
		_restore?.Dispose();
		_restore = null;
		_decoder.Dispose();
		_factory.Dispose();
	}
}
