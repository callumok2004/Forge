using D2D = Vortice.Direct2D1;
using DWrite = Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.DirectComposition;

using Forge.Common.Render;

namespace Forge.Render;

public sealed class RenderDevice : IRenderDevice, ICompositionHost, IDisposable
{
	private readonly ID3D11Device _d3dDevice = null!;
	private readonly ID3D11DeviceContext _d3dContext = null!;
	private static readonly D2D.BitmapProperties1 TargetProperties = new(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, D2D.BitmapOptions.Target | D2D.BitmapOptions.CannotDraw);

	private readonly D2D.ID2D1Factory1 _d2dFactory = null!;
	private readonly D2D.ID2D1Device _d2dDevice = null!;
	private D2D.ID2D1Bitmap1? _target;
	private IDCompositionSurface _surface = null!;
	private bool _drawn;
	private readonly IDCompositionDevice _composition = null!;
	private readonly IDCompositionTarget _compositionTarget = null!;
	private readonly IDCompositionVisual _root = null!;
	private readonly IDCompositionVisual _content = null!;
	private readonly IDCompositionVisual _webviews = null!;
	private readonly Dictionary<nint, IDCompositionVisual> _visuals = [];

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
		_composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
		_composition.CreateTargetForHwnd(hwnd, true, out _compositionTarget).CheckError();
		_composition.CreateVisual(out _root).CheckError();
		_composition.CreateVisual(out _webviews).CheckError();
		_composition.CreateVisual(out _content).CheckError();

		CreateSurface(Math.Max(width, 1), Math.Max(height, 1));
		_root.AddVisual(_webviews, false, null).CheckError();
		_root.AddVisual(_content, true, _webviews).CheckError();
		_compositionTarget.SetRoot(_root).CheckError();
		_composition.Commit().CheckError();

		_d2dFactory = D2D.D2D1.D2D1CreateFactory<D2D.ID2D1Factory1>(D2D.FactoryType.SingleThreaded);
		_d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
		Context = _d2dDevice.CreateDeviceContext(D2D.DeviceContextOptions.None);

		Context.Dpi = new Vortice.Mathematics.Size(96f, 96f);

		DWriteFactory = DWrite.DWrite.DWriteCreateFactory<DWrite.IDWriteFactory>();
	}

	public D2D.ID2D1DeviceContext Context { get; private set; } = null!;

	public D2D.ID2D1Factory1 Factory => _d2dFactory;

	internal ID3D11Device D3DDevice => _d3dDevice;

	internal ID3D11DeviceContext D3DContext => _d3dContext;

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
		_drawn = false;
		LastFailure = null;
	}

	public bool BeginPass(int left, int top, int right, int bottom) {
		try {
			using IDXGISurface surface = _surface.BeginDraw<IDXGISurface>(new Vortice.RawRect(left, top, right, bottom), out Vortice.Mathematics.Int2 offset);
			_target = Context.CreateBitmapFromDxgiSurface(surface, TargetProperties);
			Context.Target = _target;
			Context.BeginDraw();
			Context.Transform = System.Numerics.Matrix3x2.CreateTranslation(offset.X - left, offset.Y - top);
			return true;
		}
		catch (SharpGen.Runtime.SharpGenException exception) {
			LastFailure = exception.Message;
			return false;
		}
	}

	public void EndPass() {
		SharpGen.Runtime.Result result = Context.EndDraw();

		if (result.Failure) LastFailure = $"0x{result.Code:X8} {result.Description}";

		Context.Transform = System.Numerics.Matrix3x2.Identity;
		Context.Target = null;
		_target?.Dispose();
		_target = null;
		_surface.EndDraw();
		_drawn = true;
	}

	private bool _commitPending;

	public void EndDraw() {
		if (!_drawn && !_commitPending) return;

		_commitPending = false;
		_composition.Commit();
	}

	public nint CreateVisual() {
		_composition.CreateVisual(out IDCompositionVisual visual).CheckError();
		_webviews.AddVisual(visual, true, null).CheckError();
		_visuals[visual.NativePointer] = visual;
		return visual.NativePointer;
	}

	public void DestroyVisual(nint visual) {
		if (!_visuals.Remove(visual, out IDCompositionVisual? removed)) return;

		_webviews.RemoveVisual(removed);
		removed.Dispose();
		Commit();
	}

	public void SetVisualBounds(nint visual, int left, int top, int width, int height) {
		if (!_visuals.TryGetValue(visual, out IDCompositionVisual? target)) return;

		target.SetOffsetX((float)left);
		target.SetOffsetY((float)top);
		target.SetClip(new Vortice.RawRectF(0f, 0f, Math.Max(width, 0), Math.Max(height, 0)));
		_commitPending = true;
	}

	public void Commit() => _composition.Commit();

	public void Clear(Theming.Color color) =>
		Context.Clear(new Vortice.Mathematics.Color4(color.R, color.G, color.B, color.A));

	public void Resize(int width, int height, float dpi) {
		width = Math.Max(width, 1);
		height = Math.Max(height, 1);

		if (width == Width && height == Height && Math.Abs(dpi - Dpi) < 0.01f) return;

		Dpi = dpi;

		CreateSurface(width, height);
		_commitPending = true;
	}

	private void CreateSurface(int width, int height) {
		_surface?.Dispose();
		_composition.CreateSurface((uint)width, (uint)height, Format.B8G8R8A8_UNorm, Vortice.DXGI.AlphaMode.Premultiplied, out _surface).CheckError();
		_content.SetContent(_surface).CheckError();
		Width = width;
		Height = height;
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		Context?.Target = null;

		_target?.Dispose();
		_surface?.Dispose();

		foreach (IDCompositionVisual visual in _visuals.Values) visual.Dispose();

		_content?.Dispose();
		_webviews?.Dispose();
		_root?.Dispose();
		_compositionTarget?.Dispose();
		_composition?.Dispose();
		Context?.Dispose();
		DWriteFactory?.Dispose();
		_d2dDevice?.Dispose();
		_d2dFactory?.Dispose();
		_d3dContext?.Dispose();
		_d3dDevice?.Dispose();
	}
}
