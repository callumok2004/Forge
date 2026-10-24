using Forge.Core;

namespace Forge.Common.Render;

public interface IAmbient
{
	void Pulse(AmbientSignal signal, float strength);

	void Begin(AmbientSignal signal);

	void End(AmbientSignal signal, bool succeeded = true);

	bool Tick(float deltaSeconds);

	void Damage(Action<PixelRect> invalidate, int width, int height);
}
