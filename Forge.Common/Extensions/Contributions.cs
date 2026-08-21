namespace Forge.Common.Extensions;

public sealed record LanguageContribution(string Id, string[] Extensions, string[] FileNames, string[] Aliases, string? ConfigurationPath);

public sealed record GrammarContribution(string? Language, string ScopeName, string Path);

public sealed record ThemeContribution(string Label, string? Id, bool IsDark, string Path);

public sealed record CommandContribution(string Command, string Title, string? Category, string? Icon);

public sealed record SnippetContribution(string Language, string Path);
