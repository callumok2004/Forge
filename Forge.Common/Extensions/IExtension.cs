namespace Forge.Common.Extensions;

public interface IExtension
{
	string GetDirectory();
	string GetIdentifier();
	string GetName();

	string? GetPublisher();
	string? GetDisplayName();
	string? GetDescription();
	string? GetVersion();
	string? GetMain();

	bool RequiresHost();

	LanguageContribution[] GetLanguages();
	GrammarContribution[] GetGrammars();
	ThemeContribution[] GetThemes();
	CommandContribution[] GetCommands();
	SnippetContribution[] GetSnippets();
}
