namespace Forge.Common.UI;

public enum MouseButton : byte
{
	Left,
	Right,
	Middle,
	Back,
	Forward,
}

[Flags]
public enum KeyModifiers : byte
{
	None = 0,
	Control = 1,
	Shift = 2,
	Alt = 4,
}

public enum CursorCode : byte
{
	Arrow,
	IBeam,
	Hand,
	SizeWestEast,
	SizeNorthSouth,
}

public enum DockType : byte
{
	None,
	Fill,
	Left,
	Right,
	Top,
	Bottom,
}

public readonly record struct Thickness(float Left, float Top, float Right, float Bottom)
{
	public static readonly Thickness Zero = new(0f, 0f, 0f, 0f);

	public static Thickness Uniform(float value) => new(value, value, value, value);
}
