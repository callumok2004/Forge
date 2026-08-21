using Forge.Theming;

namespace Forge.Syntax.TextMate;

public static class Grammars
{
	private static readonly Dictionary<string, Grammar?> Loaded = [with(StringComparer.OrdinalIgnoreCase)];
	private static readonly Lock Sync = new();

	private static ScopeTheme? _theme;
	private static bool _themeLoaded;

	private static string AssetDirectory =>
		Path.Combine(AppContext.BaseDirectory, "Assets");

	public static Grammar? For(LanguageDefinition language) =>
		language.GrammarId is { } id ? ForId(id) : null;

	public static Grammar? ForId(string id) {
		if (GrammarIndex.ForId(id) is not { } entry) return null;

		lock (Sync) {
			if (Loaded.TryGetValue(entry.File, out Grammar? cached)) return cached;

			Grammar? grammar = Grammar.Load(Path.Combine(GrammarIndex.Directory, entry.File));
			Loaded[entry.File] = grammar;
			return grammar;
		}
	}

	public static ScopeTheme? Theme {
		get {
			lock (Sync) {
				if (_themeLoaded) return _theme;

				_theme = ScopeTheme.Load(Path.Combine(AssetDirectory, "callumtheme-color-theme.json"));
				_themeLoaded = true;
				return _theme;
			}
		}
	}

	public static Color ColorFor(IReadOnlyList<string> scopes) =>
		Theme?.ColorFor(scopes) ?? Theming.Theme.EditorForeground;
}
