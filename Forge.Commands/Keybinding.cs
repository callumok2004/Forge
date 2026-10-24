namespace Forge.Commands;

public readonly record struct Keybinding(uint Key, bool Control, bool Shift, bool Alt)
{
	public static Keybinding Normalise(uint key, bool control, bool shift, bool alt) => new(Canonical(key), control, shift, alt);

	public static bool TryParse(string? text, out Keybinding keybinding) {
		keybinding = default;

		if (text is not { Length: > 0 } || text.Contains(' ')) return false;

		bool control = false;
		bool shift = false;
		bool alt = false;
		uint key = 0;

		foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries)) {
			switch (part) {
				case "Ctrl":
					control = true;
					break;

				case "Shift":
					shift = true;
					break;

				case "Alt":
					alt = true;
					break;

				default:
					key = KeyFor(part);
					break;
			}
		}

		if (key == 0) return false;

		keybinding = new Keybinding(key, control, shift, alt);
		return true;
	}

	private static uint KeyFor(string name) => name switch {
		{ Length: 1 } when name[0] is >= 'A' and <= 'Z' => name[0],
		{ Length: 1 } when name[0] is >= '0' and <= '9' => name[0],
		['F', .. string number] when int.TryParse(number, out int function) && function is >= 1 and <= 24 => (uint)(0x6F + function),
		"=" => 0xBB,
		"-" => 0xBD,
		"`" => 0xC0,
		"," => 0xBC,
		"." => 0xBE,
		"/" => 0xBF,
		"\\" => 0xDC,
		"[" => 0xDB,
		"]" => 0xDD,
		";" => 0xBA,
		"'" => 0xDE,
		"Enter" => 0x0D,
		"Escape" => 0x1B,
		"Tab" => 0x09,
		"Space" => 0x20,
		"Backspace" => 0x08,
		"Delete" => 0x2E,
		"Insert" => 0x2D,
		"Home" => 0x24,
		"End" => 0x23,
		"PageUp" => 0x21,
		"PageDown" => 0x22,
		"Left" => 0x25,
		"Up" => 0x26,
		"Right" => 0x27,
		"Down" => 0x28,
		_ => 0,
	};

	private static uint Canonical(uint key) => key switch {
		0x6B => 0xBB,
		0x6D => 0xBD,
		>= 0x60 and <= 0x69 => key - 0x60 + '0',
		_ => key,
	};
}
