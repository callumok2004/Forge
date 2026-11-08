using Forge.Common.Commands;
using Forge.Shell;

namespace Forge.Tests;

public class JsonSchemaFeaturesTests
{
	private static readonly string Root = Path.Combine(Path.GetTempPath(), "forge-json-schema");

	private static async Task<IReadOnlyList<CompletionEntry>> Complete(string file, string text) {
		int caret = text.IndexOf('|');
		string content = text.Remove(caret, 1);
		string before = content[..caret];
		int line = before.Count(static c => c == '\n');
		int column = caret - (before.LastIndexOf('\n') + 1);
		string path = Path.Combine(Root, ".vscode", file);
		JsonSchemaFeatures features = new(_ => content, () => Root);

		Assert.True(features.Handles(path));

		return (await features.CompletionsAsync(path, line + 1, column + 1, '\0', CompletionTrigger.Invoked)).Items;
	}

	[Fact]
	public async Task LaunchConfigurationKeysAreSuggested() {
		IReadOnlyList<CompletionEntry> items = await Complete("launch.json", "{\n\t\"configurations\": [\n\t\t{\n\t\t\t\"name\": \"x\",\n\t\t\t|\n\t\t}\n\t]\n}");

		Assert.Contains(items, item => item.Label == "console");
		Assert.DoesNotContain(items, item => item.Label == "name");
	}

	[Fact]
	public async Task LaunchConsoleValuesAreSuggested() {
		IReadOnlyList<CompletionEntry> items = await Complete("launch.json", "{\n\t\"configurations\": [\n\t\t{\n\t\t\t\"console\": |\n\t\t}\n\t]\n}");

		Assert.Contains(items, item => item.InsertText == "\"externalTerminal\"");
	}

	[Fact]
	public async Task TaskKeysInsideAStringReplaceFromTheQuote() {
		IReadOnlyList<CompletionEntry> items = await Complete("tasks.json", "{\n\t\"tasks\": [\n\t\t{\n\t\t\t\"lab|\"\n\t\t}\n\t]\n}");
		CompletionEntry label = Assert.Single(items, item => item.Label == "label");

		Assert.Equal(4, label.ReplaceStartColumn);
		Assert.True(label.ReplacesToEnd);
		Assert.Equal("\"label\": \"$0\"", label.InsertText);
	}

	[Fact]
	public async Task CommentsAreSkipped() {
		IReadOnlyList<CompletionEntry> items = await Complete("tasks.json", "{\n\t// \"tasks\": [ {\n\t|\n}");

		Assert.Contains(items, item => item.Label == "tasks");
	}
}
