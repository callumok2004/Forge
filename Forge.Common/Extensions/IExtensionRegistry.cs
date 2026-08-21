namespace Forge.Common.Extensions;

public interface IExtensionRegistry
{
	IReadOnlyList<IExtension> All();
	IReadOnlyList<ThemeContribution> Themes();
	IReadOnlyList<CommandContribution> Commands();
	IEnumerable<IExtension> RequiringHost();

	void ScanDirectory(string root);
	void Add(IExtension extension);

	LanguageContribution? LanguageFor(string path);
	GrammarContribution? GrammarFor(string languageId);
	ThemeContribution? ThemeNamed(string label);
}
