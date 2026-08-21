namespace Forge.Common.Commands;

public interface ILanguageFeatures
{
	bool Handles(string path);

	Task<CompletionList> CompletionsAsync(string path, int line, int column, char trigger, CompletionTrigger kind);

	Task<CompletionDetail?> ResolveCompletionAsync(int index);

	Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column, char trigger);

	Task<string?> HoverAsync(string path, int line, int column);
}
