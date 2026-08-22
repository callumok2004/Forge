using System.Text.Json.Nodes;

using Forge.Commands;
using Forge.Common.Commands;
using Forge.VSCode;

namespace Forge.Tests;

public class VSCodeHostTests
{
	private static string? ClaudeExtensionDirectory {
		get {
			if (!Directory.Exists(VSCodeExtensions.DefaultDirectory)) return null;

			foreach (string directory in Directory.EnumerateDirectories(VSCodeExtensions.DefaultDirectory, "anthropic.claude-code*")) {
				return directory;
			}

			return null;
		}
	}

	[Fact]
	public void RuntimeShipsBesideTheBinary() {
		Assert.True(File.Exists(NodeExtensionHost.RuntimePath), NodeExtensionHost.RuntimePath);
		Assert.True(File.Exists(NodeExtensionHost.BootstrapPath), NodeExtensionHost.BootstrapPath);
	}

	[Fact]
	public void ReadsAVSCodeManifest() {
		if (ClaudeExtensionDirectory is not { } directory) return;

		VSCodeExtension? extension = VSCodeExtensions.Read(directory);

		Assert.NotNull(extension);
		Assert.Equal("anthropic.claude-code", extension!.Identifier, ignoreCase: true);
		Assert.True(extension.HasWebview);
	}

	[Fact]
	public async Task ActivatesTheClaudeExtensionAndResolvesItsWebview() {
		string directory = Assert.IsType<string>(ClaudeExtensionDirectory);

		string storage = Path.Combine(Path.GetTempPath(), "forge-extension-host-tests");

		using NodeExtensionHost host = new(storage);

		List<string> log = [];
		host.Log = line => log.Add(line);

		Assert.True(host.Start(new HostState {
			WorkspacePath = Directory.GetCurrentDirectory(),
		}), string.Join("\n", log));

		ActivationResult? result = await host.ActivateAsync(directory).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal("anthropic.claude-code", result!.Id, ignoreCase: true);
		Assert.NotEmpty(result.Commands);
		Assert.Contains(result.WebviewProviders, id => id.Contains("Sidebar", StringComparison.Ordinal));

		string viewId = result.WebviewProviders.First(id => id.Contains("Sidebar", StringComparison.Ordinal));

		string? html = await host.ResolveWebviewAsync(viewId).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		Assert.NotNull(html);
		Assert.Contains("<!DOCTYPE html>", html!, StringComparison.OrdinalIgnoreCase);
		Assert.True(html!.Length > 1000, $"webview html was only {html.Length} bytes");
	}

	[Fact]
	public async Task BridgeRegistersExtensionCommandsIntoThePalette() {
		string directory = Assert.IsType<string>(ClaudeExtensionDirectory);

		CommandRegistry registry = new();
		RecordingHost host = new();

		using VSCodeBridge bridge = new(registry, host);

		bridge.Start(
			Path.Combine(Path.GetTempPath(), "forge-vscode-bridge-tests"),
			new HostState { WorkspacePath = Directory.GetCurrentDirectory() },
			Path.GetDirectoryName(directory)!);

		Assert.True(bridge.IsRunning, string.Join(Environment.NewLine, host.Lines));

		for (int i = 0; i < 100 && bridge.Activated.Count == 0; i++) {
			await Task.Delay(100, TestContext.Current.CancellationToken);
		}

		Assert.NotEmpty(bridge.Activated);
		Assert.NotEmpty(registry.All());
		Assert.Contains(registry.All(), command => command.GetId().StartsWith("claude", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void SerialisesEditorStateTheWayTheShimExpects() {
		HostEditor editor = new(@"c:\work\Program.cs", 3, 7, 3, 12);

		JsonObject json = editor.ToJson();

		Assert.Equal("file", json["document"]!["uri"]!["scheme"]!.GetValue<string>());
		Assert.Equal("/c:/work/Program.cs", json["document"]!["uri"]!["path"]!.GetValue<string>());
		Assert.Equal("cs", json["document"]!["languageId"]!.GetValue<string>());
		Assert.Equal(7, json["selection"]!["start"]!["character"]!.GetValue<int>());
		Assert.False(json["selection"]!["isEmpty"]!.GetValue<bool>());
	}
}

internal sealed class RecordingHost : ICommandHost
{
	public List<string> Lines { get; } = [];

	private readonly OutputChannels _channels = new();

	public string? GetWorkspacePath() => Directory.GetCurrentDirectory();

	public string? GetActiveFilePath() => null;

	public int GetCaretLine() => 1;

	public int GetCaretColumn() => 1;

	public int GetLineCount() => 0;

	public int GetErrorCount() => 0;

	public string? GetBranchName() => null;

	public string? GetRemoteUrl() => null;

	public void Log(string line) => Lines.Add(line);

	public IOutputChannel Channel(string name) => _channels.Get(name);

	public void Refresh() {
	}

	public void OpenFile(string path) {
	}

	public void GoTo(string path, int line, int column) {
	}

	public void ShowReferences(string title, IReadOnlyList<HostLocation> locations) {
	}

	public bool EditActiveDocument(Func<string, string?> transform) => false;

	public void SetSemanticTokens(string path, IReadOnlyList<SemanticToken> tokens) {
	}

	public void ReportProblems(IReadOnlyList<Diagnostic> problems) {
	}

	public void ShowPanel(string view) {
	}

	public void ShowOutput(IOutputChannel channel) {
	}

	public void RunInTerminal(string name, string commandLine, string? workingDirectory,
		Action<string>? lines, Action<int>? finished) {
	}

	public void ShowCommandPalette() {
	}

	public void SetStatusItem(string id, string icon, string text, string? commandId, bool rightAligned = false, int priority = 0) {
	}

	public void RemoveStatusItem(string id) {
	}

	public void ObserveDocuments(Action<DocumentEvent, string, string?> observer) {
	}

	public void ProvideLanguageFeatures(ILanguageFeatures features) {
	}
}
