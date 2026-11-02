namespace Forge.Common.UI;

public static class TextCase
{
	public static string Capitalize(string text) {
		bool start = true;
		int first = -1;

		for (int i = 0; i < text.Length; i++) {
			char c = text[i];

			if (char.IsWhiteSpace(c)) {
				start = true;
				continue;
			}

			if (!start) continue;

			start = false;

			if (char.IsLower(c)) {
				first = i;
				break;
			}
		}

		if (first < 0) return text;

		return string.Create(text.Length, (text, first), static (span, state) => {
			state.text.AsSpan().CopyTo(span);
			bool start = true;

			for (int i = state.first; i < span.Length; i++) {
				if (char.IsWhiteSpace(span[i])) {
					start = true;
					continue;
				}

				if (start) span[i] = char.ToUpperInvariant(span[i]);

				start = false;
			}
		});
	}
}
