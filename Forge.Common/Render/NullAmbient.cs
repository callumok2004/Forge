using Forge.Core;

namespace Forge.Common.Render;

public sealed class NullAmbient : IAmbient
{
	public static readonly NullAmbient Instance = new();

	public void Pulse(AmbientSignal signal, float strength) { }

	public void Begin(AmbientSignal signal) { }

	public void End(AmbientSignal signal, bool succeeded = true) { }

	public bool Tick(float deltaSeconds) => false;

	public void Damage(Action<PixelRect> invalidate, int width, int height) { }
}
