using Forge.Common.Extensions;

namespace Forge.Extensions.Host;

public sealed class ExtensionRegistry : IExtensionRegistry
{
	private readonly Dictionary<string, LanguageContribution> _byFileExtension = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, LanguageContribution> _byFileName = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, GrammarContribution> _grammarsByLanguage = [with(StringComparer.OrdinalIgnoreCase)];

	private readonly List<IExtension> _extensions = [];
	private readonly List<ThemeContribution> _themes = [];
	private readonly List<CommandContribution> _commands = [];

	public IReadOnlyList<IExtension> All() => _extensions;

	public IReadOnlyList<ThemeContribution> Themes() => _themes;

	public IReadOnlyList<CommandContribution> Commands() => _commands;

	public IEnumerable<IExtension> RequiringHost() => _extensions.Where(static extension => extension.RequiresHost());

	public void ScanDirectory(string root) {
		if (!Directory.Exists(root)) return;

		try {
			foreach (string directory in Directory.EnumerateDirectories(root)) {
				if (ExtensionManifest.Load(directory) is { } manifest) Add(manifest);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	public void Add(IExtension extension) {
		_extensions.Add(extension);

		foreach (LanguageContribution language in extension.GetLanguages()) {
			foreach (string suffix in language.Extensions) _byFileExtension.TryAdd(suffix, language);

			foreach (string fileName in language.FileNames) _byFileName.TryAdd(fileName, language);
		}

		foreach (GrammarContribution grammar in extension.GetGrammars()) {
			if (grammar.Language is { } language) _grammarsByLanguage.TryAdd(language, grammar);
		}

		_themes.AddRange(extension.GetThemes());
		_commands.AddRange(extension.GetCommands());
	}

	public LanguageContribution? LanguageFor(string path) {
		string fileName = Path.GetFileName(path);

		if (_byFileName.TryGetValue(fileName, out LanguageContribution? byName)) return byName;

		string extension = Path.GetExtension(fileName);

		return extension.Length > 0 && _byFileExtension.TryGetValue(extension, out LanguageContribution? found) ? found : null;
	}

	public GrammarContribution? GrammarFor(string languageId) =>
		_grammarsByLanguage.TryGetValue(languageId, out GrammarContribution? grammar) ? grammar : null;

	public ThemeContribution? ThemeNamed(string label) =>
		_themes.FirstOrDefault(theme => string.Equals(theme.Label, label, StringComparison.OrdinalIgnoreCase));
}
