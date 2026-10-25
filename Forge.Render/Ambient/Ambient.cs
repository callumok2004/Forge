using System.Numerics;
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
	private const int SlotCount = 5;
	private const float GlowHeightFraction = 0.38f;
	private const float ResultSeconds = 4f;
	private const float TransitionRate = 5f;
	private const float BreathSeconds = 1.6f;

	private static readonly float[] AttackRates = [3.0f, 3.0f, 3.0f, 6.0f, 3.0f];

	private static readonly float[] DecayRates = [0.55f, 0.55f, 0.60f, 0.90f, 0.55f];

	private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	private readonly Slot[] _slots = new Slot[SlotCount];
	private readonly int[] _outstanding = new int[SlotCount];
	private readonly Lock _sync = new();

	private Vector4 _glow;
	private float _depth;
	private Color _result;
	private float _resultSeconds;

	private bool _glowMoving;
	private bool _loaderMoving;

	private ID3D11VertexShader? _vertexShader;
	private ID3D11PixelShader? _pixelShader;
	private ID3D11Buffer? _constants;
	private ID3D11Texture2D? _texture;
	private ID3D11RenderTargetView? _view;
	private D2D.ID2D1Bitmap1? _bitmap;

	private int _width;
	private int _height;
	private int _band;
	private bool _failed;
	private bool _disposed;

	public Ambient() {
		for (int i = 0; i < SlotCount; i++) {
			_slots[i].Attack = AttackRates[i];
			_slots[i].Decay = DecayRates[i];
		}
	}

	public string? LastFailure { get; private set; }

	public void Pulse(AmbientSignal signal, float strength) {
		lock (_sync) {
			ref Slot slot = ref _slots[Index(signal)];
			slot.Sustained = false;
			slot.Target = Math.Clamp(Math.Max(slot.Target, strength), 0f, 1f);
		}
	}

	public void Begin(AmbientSignal signal) {
		lock (_sync) {
			int index = Index(signal);
			_outstanding[index]++;
			_slots[index].Sustained = true;
			_slots[index].Target = 1f;
		}
	}

	public void End(AmbientSignal signal, bool succeeded = true) {
		lock (_sync) {
			int index = Index(signal);
			int remaining = _outstanding[index] = Math.Max(_outstanding[index] - 1, 0);

			if (remaining > 0) return;

			_slots[index].Target = 0f;

			if (signal is AmbientSignal.Build or AmbientSignal.Tests) {
				_result = succeeded ? Theme.ChartsGreen : Theme.ChartsRed;
				_resultSeconds = ResultSeconds;
			}
		}
	}

	public bool Tick(float deltaSeconds) {
		float dt = deltaSeconds > 0f && deltaSeconds < 0.25f ? deltaSeconds : 1f / 60f;

		lock (_sync) {
			bool loading = false;

			for (int i = 0; i < SlotCount; i++) {
				ref Slot slot = ref _slots[i];

				if (!slot.Sustained && slot.Target > 0f) {
					slot.Target *= MathF.Exp(-slot.Decay * dt);
					if (slot.Target < 0.002f) slot.Target = 0f;
				}

				slot.Level += (slot.Target - slot.Level) * (1f - MathF.Exp(-slot.Attack * dt));

				if (slot.Target <= 0f && slot.Level < 0.001f) slot.Level = 0f;

				if (i != Index(AmbientSignal.Debug) && slot.Level > 0.001f) loading = true;
			}

			_loaderMoving = loading;

			if (_resultSeconds > 0f) _resultSeconds = MathF.Max(_resultSeconds - dt, 0f);

			bool building = _outstanding[Index(AmbientSignal.Build)] + _outstanding[Index(AmbientSignal.Tests)] > 0;

			bool debugging = _outstanding[Index(AmbientSignal.Debug)] > 0;
			(Vector4 target, float depth) = building ? (Opaque(Theme.ChartsYellow), 1f) : _resultSeconds > 0f ? (Opaque(_result), 0f) : debugging ? (Opaque(Theme.ChartsBlue), 0f) : (Vector4.Zero, 0f);

			float blend = 1f - MathF.Exp(-TransitionRate * dt);
			_glow = Vector4.Lerp(_glow, target, blend);
			_depth += (depth - _depth) * blend;

			bool settled = Vector4.Distance(_glow, target) < 0.002f && MathF.Abs(depth - _depth) < 0.002f;

			if (settled) {
				_glow = target;
				_depth = depth;
			}

			_glowMoving = !settled || (_depth > 0f && _glow.W > 0f) || _resultSeconds > 0f;

			return !_failed && (_glowMoving || _loaderMoving);
		}
	}

	public void Damage(Action<PixelRect> invalidate, int width, int height) {
		if (_failed || width <= 0 || height <= 0) return;

		if (_glowMoving) {
			int band = Math.Clamp((int)MathF.Ceiling(height * GlowHeightFraction), 1, height);
			invalidate(new PixelRect(0, height - band, width, height));
			return;
		}

		if (_loaderMoving) {
			int loader = Math.Clamp((int)(height * 0.03f), 8, height);
			invalidate(new PixelRect(0, height - loader, width, height));
		}
	}

	public void Prepare(RenderDevice device) {
		if (_failed || !EnsureResources(device)) return;

		ID3D11DeviceContext context = device.D3DContext;

		float time = (float)_clock.Elapsed.TotalSeconds;
		float breath = 0.7f + (0.3f * MathF.Cos(MathF.Tau * time / BreathSeconds));

		Constants data;

		lock (_sync) {
			float strength = 1f - (_depth * (1f - breath));
			Vector4 loader = _glow.W > 0.01f ? _glow / _glow.W : Opaque(Theme.Accent);

			data = new Constants {
				Frame = new Vector4(_width, _height, time, _band),
				Glow = _glow * strength,
				Loader = loader with { W = LoaderProgress() },
			};
		}

		context.UpdateSubresource(data, _constants!);

		context.ClearState();
		context.OMSetRenderTargets(_view!);
		context.RSSetViewport(0f, 0f, _width, _band);
		context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
		context.VSSetShader(_vertexShader);
		context.PSSetShader(_pixelShader);
		context.PSSetConstantBuffer(0, _constants);
		context.Draw(3, 0);
		context.ClearState();
	}

	public void Composite(RenderDevice device) {
		if (_failed || _bitmap is null) return;

		device.Context.DrawImage(_bitmap, new Vector2(0f, _height - _band),
			D2D.InterpolationMode.Linear, D2D.CompositeMode.SourceOver);
	}

	private float LoaderProgress() {
		float progress = 0f;

		for (int i = 0; i < SlotCount; i++) {
			if (i == Index(AmbientSignal.Debug)) continue;

			progress = 1f - ((1f - progress) * (1f - _slots[i].Level));
		}

		return progress;
	}

	private static Vector4 Opaque(Color color) => new(color.R, color.G, color.B, 1f);

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
		_band = Math.Clamp((int)MathF.Ceiling(height * GlowHeightFraction), 1, height);

		_texture = device.D3DDevice.CreateTexture2D(new Texture2DDescription {
			Width = (uint)width,
			Height = (uint)_band,
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
		public Vector4 Glow;
		public Vector4 Loader;
	}
}
