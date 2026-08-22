using Forge.Core;
using Forge.Theming;

namespace Forge.Common.Render;

public interface IAmbient
{
	void Set(AmbientSignal signal, float level);

	void Pulse(AmbientSignal signal, float strength);

	void Begin(AmbientSignal signal);

	void End(AmbientSignal signal);

	void SetPointer(float x, float y);

	void SetFocused(bool focused);

	void SetAccent(Color color);

	void SetLanguageColor(Color color, float strength);

	void SetOpacity(float opacity);

	void Touch();

	bool Tick(float deltaSeconds);

	void Damage(Action<PixelRect> invalidate, int width, int height);
}
