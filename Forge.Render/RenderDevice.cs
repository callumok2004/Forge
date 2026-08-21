using D2D = Vortice.Direct2D1;
using DWrite = Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.Direct3D;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class RenderDevice : IRenderDevice, IDisposable
{
	private readonly ID3D11Device _d3dDevice = null!;
	private readonly ID3D11DeviceContext _d3dContext = null!;
	private readonly IDXGISwapChain1 _swapChain = null!;
	private readonly D2D.ID2D1Factory1 _d2dFactory = null!;
	private readonly D2D.ID2D1Device _d2dDevice = null!;
	private D2D.ID2D1Bitmap1? _target;
	private D2D.ID2D1Bitmap1? _surface;

	private bool _disposed;

	public RenderDevice(nint hwnd, int width, int height, float dpi) {
		Dpi = dpi;

		DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport;

		FeatureLevel[] featureLevels =
		[
			FeatureLevel.Level_11_1,
			FeatureLevel.Level_11_0,
			FeatureLevel.Level_10_1,
			FeatureLevel.Level_10_0,
		];

		D3D11.D3D11CreateDevice(
			null,
			DriverType.Hardware,
			flags,
			featureLevels,
			out _d3dDevice,
			out _d3dContext).CheckError();

		using IDXGIDevice dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
		using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
		using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

		SwapChainDescription1 description = new() {
			Width = (uint)Math.Max(width, 1),
			Height = (uint)Math.Max(height, 1),
			Format = Format.B8G8R8A8_UNorm,
			Stereo = false,
			SampleDescription = new SampleDescription(1, 0),
			BufferUsage = Usage.RenderTargetOutput,
			BufferCount = 2,
			Scaling = Scaling.None,
			SwapEffect = SwapEffect.FlipSequential,
			AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
			Flags = SwapChainFlags.None,
		};

		_swapChain = factory.CreateSwapChainForHwnd(_d3dDevice, hwnd, description);

		factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

		_d2dFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>(D2D.FactoryType.SingleThreaded);
		_d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
		Context = _d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);

		DWriteFactory = DWrite.DWrite.DWriteCreateFactory<DWrite.IDWriteFactory>();

		CreateTargetFromSwapChain();
	}

	public D2D.ID2D1DeviceContext Context { get; private set; } = null!;

	public D2D.ID2D1Factory1 Factory => _d2dFactory;

	public DWrite.IDWriteFactory DWriteFactory { get; private set; } = null!;

	public float Dpi { get; private set; }

	public int Width { get; private set; }

	public int Height { get; private set; }

	public string? LastFailure { get; private set; }

	public int GetWidth() => Width;

	public int GetHeight() => Height;

	public float GetDpi() => Dpi;

	public string? GetLastFailure() => LastFailure;

	public void BeginDraw() {
		Context.Target = _surface;
		Context.BeginDraw();
	}

	public void EndDraw() {
		SharpGen.Runtime.Result result = Context.EndDraw();

		if (result.Failure) {
			LastFailure = $"0x{result.Code:X8} {result.Description}";
			return;
		}

		Context.Target = _target;
		Context.BeginDraw();

		if (_surface is not null) {
			Context.DrawImage(_surface, new System.Numerics.Vector2(0f, 0f),
				D2D.InterpolationMode.NearestNeighbor, D2D.CompositeMode.SourceCopy);
		}

		result = Context.EndDraw();

		if (result.Failure) {
			LastFailure = $"0x{result.Code:X8} {result.Description}";
			return;
		}

		LastFailure = null;

		_swapChain.Present(0, PresentFlags.None);
	}

	public void Clear(Theming.Color color) =>
		Context.Clear(new Vortice.Mathematics.Color4(color.R, color.G, color.B, color.A));

	public void Resize(int width, int height, float dpi) {
		width = Math.Max(width, 1);
		height = Math.Max(height, 1);

		if (width == Width && height == Height && Math.Abs(dpi - Dpi) < 0.01f) return;

		Dpi = dpi;

		Context.Target = null;
		_target?.Dispose();
		_target = null;
		_surface?.Dispose();
		_surface = null;

		_swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
		CreateTargetFromSwapChain();
	}

	private void CreateTargetFromSwapChain() {
		using IDXGISurface surface = _swapChain.GetBuffer<IDXGISurface>(0);

		D2D.BitmapProperties1 properties = new(
			new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
			96f,
			96f,
			D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw);

		_target = Context.CreateBitmapFromDxgiSurface(surface, properties);
		Context.Target = _target;

		Context.Dpi = new Vortice.Mathematics.Size(96f, 96f);

		SwapChainDescription1 description = _swapChain.Description1;
		Width = (int)description.Width;
		Height = (int)description.Height;

		_surface = Context.CreateBitmap(
			new Vortice.Mathematics.SizeI(Math.Max(Width, 1), Math.Max(Height, 1)),
			nint.Zero,
			0,
			new D2D.BitmapProperties1(
				new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore),
				96f,
				96f,
				D2D.BitmapOptions.Target));
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		Context?.Target = null;

		_target?.Dispose();
		Context?.Dispose();
		DWriteFactory?.Dispose();
		_d2dDevice?.Dispose();
		_d2dFactory?.Dispose();
		_swapChain?.Dispose();
		_d3dContext?.Dispose();
		_d3dDevice?.Dispose();
	}
}
