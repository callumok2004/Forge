using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

using D2D = Vortice.Direct2D1;

using Forge.Common.Render;
using Forge.Core;
using Forge.Theming;

namespace Forge.Render;

public sealed class Ambient : IAmbient, IDisposable
{
	private const int SlotCount = 12;
	private const float IdleAfterSeconds = 12f;
	private const float BandFraction = 0.22f;

	private static readonly float[] AttackRates =
		[3.0f, 3.0f, 3.0f, 4.5f, 4.0f, 3.5f, 5.0f, 9.0f, 6.0f, 6.0f, 3.0f, 3.0f];

	private static readonly float[] DecayRates =
		[0.55f, 0.55f, 0.55f, 0.30f, 0.30f, 0.45f, 0.70f, 2.20f, 0.90f, 0.55f, 0.60f, 0.60f];

	private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	private readonly Slot[] _slots = new Slot[SlotCount];
	private readonly int[] _outstanding = new int[SlotCount];

	private Vector2 _pointer = new(0.5f, 0.5f);
	private Vector2 _pointerTarget = new(0.5f, 0.5f);
	private Vector4 _accent = new(0.32f, 0.45f, 0.78f, 1f);
	private Vector4 _language;
	private Vector4 _languageTarget;
	private float _opacity = 1f;
	private float _time;
	private float _idle;
	private float _idleSeconds;
	private float _focus = 1f;
	private bool _focused = true;
	private bool _active = true;
	private bool _rimMoving = true;
	private bool _loaderMoving;

	private ID3D11VertexShader? _vertexShader;
	private ID3D11PixelShader? _pixelShader;
	private ID3D11Buffer? _constants;
	private ID3D11Texture2D? _texture;
	private ID3D11RenderTargetView? _view;
	private D2D.ID2D1Bitmap1? _bitmap;

	private int _width;
	private int _height;
	private bool _failed;
	private bool _disposed;

	public Ambient() {
		for (int i = 0; i < SlotCount; i++) {
			_slots[i].Attack = AttackRates[i];
			_slots[i].Decay = DecayRates[i];
		}
	}

	public string? LastFailure { get; private set; }

	public void Set(AmbientSignal signal, float level) {
		ref Slot slot = ref Reference(signal);
		slot.Sustained = true;
		slot.Target = Math.Clamp(level, 0f, 1f);
		_active = true;
	}

	public void Pulse(AmbientSignal signal, float strength) {
		ref Slot slot = ref Reference(signal);
		slot.Sustained = false;
		slot.Target = Math.Clamp(Math.Max(slot.Target, strength), 0f, 1f);
		_active = true;
	}

	public void Begin(AmbientSignal signal) {
		int index = Index(signal);

		lock (_outstanding) _outstanding[index]++;

		Set(signal, 1f);
	}

	public void End(AmbientSignal signal) {
		int index = Index(signal);
		int remaining;

		lock (_outstanding) remaining = _outstanding[index] = Math.Max(_outstanding[index] - 1, 0);

		if (remaining == 0) Set(signal, 0f);
	}

	public void SetPointer(float x, float y) {
		_pointerTarget = new Vector2(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f));
		Touch();
	}

	public void SetFocused(bool focused) {
		_focused = focused;
		_active = true;
	}

	public void SetAccent(Color color) => _accent = new Vector4(color.R, color.G, color.B, 1f);

	public void SetLanguageColor(Color color, float strength) {
		_languageTarget = new Vector4(color.R, color.G, color.B, Math.Clamp(strength, 0f, 1f));
		_active = true;
	}

	public void SetOpacity(float opacity) => _opacity = Math.Clamp(opacity, 0f, 1f);

	public void Touch() {
		_idleSeconds = 0f;
		_active = true;
	}

	public bool Tick(float deltaSeconds) {
		float dt = deltaSeconds > 0f && deltaSeconds < 0.25f ? deltaSeconds : 1f / 60f;

		_idleSeconds += dt;

		bool moving = false;

		for (int i = 0; i < SlotCount; i++) {
			ref Slot slot = ref _slots[i];

			if (!slot.Sustained && slot.Target > 0f) {
				slot.Target *= MathF.Exp(-slot.Decay * dt);
				if (slot.Target < 0.002f) slot.Target = 0f;
			}

			slot.Level += (slot.Target - slot.Level) * (1f - MathF.Exp(-slot.Attack * dt));

			if (slot.Target <= 0f && slot.Level < 0.001f) slot.Level = 0f;

			if (slot.Level > 0f || slot.Target > 0f) moving = true;
		}

		float focusTarget = _focused ? 1f : 0f;
		float idleTarget = Math.Clamp((_idleSeconds - IdleAfterSeconds) / 8f, 0f, 1f);

		_focus += (focusTarget - _focus) * (1f - MathF.Exp(-6f * dt));
		_idle += (idleTarget - _idle) * (1f - MathF.Exp(-0.5f * dt));
		_pointer = Vector2.Lerp(_pointer, _pointerTarget, 1f - MathF.Exp(-10f * dt));
		_language = Vector4.Lerp(_language, _languageTarget, 1f - MathF.Exp(-4.5f * dt));

		bool settled = !moving
			&& MathF.Abs(focusTarget - _focus) < 0.004f
			&& MathF.Abs(idleTarget - _idle) < 0.004f
			&& Vector2.Distance(_pointer, _pointerTarget) < 0.002f
			&& Vector4.Distance(_language, _languageTarget) < 0.004f;

		if (settled) {
			_focus = focusTarget;
			_idle = idleTarget;
			_pointer = _pointerTarget;
			_language = _languageTarget;
		}

		_rimMoving = !settled;
		_loaderMoving = _slots[Index(AmbientSignal.Build)].Level > 0.001f
			|| _slots[Index(AmbientSignal.Task)].Level > 0.001f
			|| _slots[Index(AmbientSignal.Tests)].Level > 0.001f
			|| _slots[Index(AmbientSignal.Search)].Level > 0.001f;

		if (settled && !_loaderMoving) _active = false;

		return _opacity > 0f && !_failed && _active;
	}

	public void Damage(Action<PixelRect> invalidate, int width, int height) {
		if (_opacity <= 0f || _failed || width <= 0 || height <= 0) return;

		int loader = Math.Clamp((int)(height * 0.03f), 8, height);

		if (!_rimMoving) {
			if (_loaderMoving) invalidate(new PixelRect(0, height - loader, width, height));

			return;
		}

		int band = Math.Clamp((int)(height * BandFraction), 1, Math.Min(width, height) / 2);

		invalidate(new PixelRect(0, 0, width, band));
		invalidate(new PixelRect(0, height - band, width, height));
		invalidate(new PixelRect(0, band, band, height - band));
		invalidate(new PixelRect(width - band, band, width, height - band));
	}

	public void Prepare(RenderDevice device) {
		if (_opacity <= 0f || _failed || !EnsureResources(device)) return;

		ID3D11DeviceContext context = device.D3DContext;

		_time = (float)_clock.Elapsed.TotalSeconds;

		Constants data = new() {
			Frame = new Vector4(_width, _height, _time, 0f),
			Pointer = new Vector4(_pointer.X, _pointer.Y, _focus, _idle),
			Accent = _accent with { W = _opacity },
			Language = _language,
			Signal0 = new Vector4(_slots[0].Level, _slots[1].Level, _slots[2].Level, _slots[3].Level),
			Signal1 = new Vector4(_slots[4].Level, _slots[5].Level, _slots[6].Level, _slots[7].Level),
			Signal2 = new Vector4(_slots[8].Level, _slots[9].Level, _slots[10].Level, _slots[11].Level),
		};

		context.UpdateSubresource(data, _constants!);

		context.ClearState();
		context.OMSetRenderTargets(_view!);
		context.RSSetViewport(0f, 0f, _width, _height);
		context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		context.VSSetShader(_vertexShader);
		context.PSSetShader(_pixelShader);
		context.PSSetConstantBuffer(0, _constants);
		context.Draw(3, 0);
		context.ClearState();
	}

	public void Composite(RenderDevice device) {
		if (_opacity <= 0f || _failed || _bitmap is null) return;

		device.Context.DrawImage(_bitmap, new Vector2(0f, 0f),
			D2D.InterpolationMode.Linear, D2D.CompositeMode.Plus);
	}

	private ref Slot Reference(AmbientSignal signal) => ref _slots[Index(signal)];

	private static int Index(AmbientSignal signal) {
		int index = (int)signal;
		return (uint)index < SlotCount ? index : 0;
	}

	private bool EnsureResources(RenderDevice device) {
		if (_vertexShader is null && !CreateShaders(device)) return false;

		int width = Math.Max(device.Width, 1);
		int height = Math.Max(device.Height, 1);

		if (_texture is not null && width == _width && height == _height) return true;

		ReleaseTarget();

		_width = width;
		_height = height;

		_texture = device.D3DDevice.CreateTexture2D(new Texture2DDescription {
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1,
			ArraySize = 1,
			Format = Format.B8G8R8A8_UNorm,
			SampleDescription = new SampleDescription(1, 0),
			Usage = ResourceUsage.Default,
			BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
		});

		_view = device.D3DDevice.CreateRenderTargetView(_texture);

		using IDXGISurface surface = _texture.QueryInterface<IDXGISurface>();

		_bitmap = device.Context.CreateBitmapFromDxgiSurface(surface, new D2D.BitmapProperties1(
			new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
			96f, 96f, D2D.BitmapOptions.None));

		return true;
	}

	private bool CreateShaders(RenderDevice device) {
		try {
			string source = ReadSource();

			ReadOnlyMemory<byte> vertex = Compile(source, "VSMain", "vs_4_0");
			ReadOnlyMemory<byte> pixel = Compile(source, "PSMain", "ps_4_0");

			_vertexShader = device.D3DDevice.CreateVertexShader(vertex.Span);
			_pixelShader = device.D3DDevice.CreatePixelShader(pixel.Span);

			_constants = device.D3DDevice.CreateBuffer(new BufferDescription(
				(uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Default));

			return true;
		}
		catch (Exception exception) {
			LastFailure = exception.Message;
			_failed = true;
			return false;
		}
	}

	private static ReadOnlyMemory<byte> Compile(string source, string entryPoint, string profile) {
		Compiler.Compile(source, entryPoint, "Ambient.hlsl", profile, out Blob? code, out Blob? errors);

		using (errors) {
			if (code is null) throw new InvalidOperationException(errors?.AsString() ?? $"{entryPoint} failed to compile");
		}

		using (code) return code.AsBytes();
	}

	private static string ReadSource() {
		using Stream? stream = typeof(Ambient).Assembly
			.GetManifestResourceStream("Forge.Render.Ambient.Ambient.hlsl");

		if (stream is null) throw new InvalidOperationException("Ambient.hlsl is missing from the assembly");

		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}

	private void ReleaseTarget() {
		_bitmap?.Dispose();
		_bitmap = null;
		_view?.Dispose();
		_view = null;
		_texture?.Dispose();
		_texture = null;
	}

	public void Dispose() {
		if (_disposed) return;

		_disposed = true;

		ReleaseTarget();
		_constants?.Dispose();
		_pixelShader?.Dispose();
		_vertexShader?.Dispose();
	}

	private struct Slot
	{
		public float Level;
		public float Target;
		public float Attack;
		public float Decay;
		public bool Sustained;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Constants
	{
		public Vector4 Frame;
		public Vector4 Pointer;
		public Vector4 Accent;
		public Vector4 Language;
		public Vector4 Signal0;
		public Vector4 Signal1;
		public Vector4 Signal2;
	}
}
