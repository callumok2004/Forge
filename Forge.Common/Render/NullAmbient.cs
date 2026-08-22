using Forge.Core;
using Forge.Theming;

namespace Forge.Common.Render;

public sealed class NullAmbient : IAmbient
{
	public static readonly NullAmbient Instance = new();

	public void Set(AmbientSignal signal, float level) { }

	public void Pulse(AmbientSignal signal, float strength) { }

	public void Begin(AmbientSignal signal) { }

	public void End(AmbientSignal signal) { }

	public void SetPointer(float x, float y) { }

	public void SetFocused(bool focused) { }

	public void SetAccent(Color color) { }

	public void SetLanguageColor(Color color, float strength) { }

	public void SetOpacity(float opacity) { }

	public void Touch() { }

	public bool Tick(float deltaSeconds) => false;

	public void Damage(Action<PixelRect> invalidate, int width, int height) { }
}
