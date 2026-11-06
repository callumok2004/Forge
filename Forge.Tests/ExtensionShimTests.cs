using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Forge.VSCode;

namespace Forge.Tests;

public class ExtensionShimTests
{
	private const string Manifest = """
		{ "name": "shim-probe", "publisher": "forge", "main": "extension.js", "activationEvents": ["*"] }
		""";

	private const string Extension = """
		const vscode = require("vscode");

		class MemoryFs {
			constructor() {
				this.files = new Map();
				this.changed = new vscode.EventEmitter();
				this.onDidChangeFile = this.changed.event;
			}
			stat(uri) { return { type: vscode.FileType.File, ctime: 0, mtime: 0, size: (this.files.get(uri.toString()) ?? new Uint8Array()).length }; }
			readFile(uri) { const value = this.files.get(uri.toString()); if (!value) throw vscode.FileSystemError.FileNotFound(uri); return value; }
			writeFile(uri, content) { this.files.set(uri.toString(), content); }
			readDirectory() { return []; }
			createDirectory() {}
			delete(uri) { this.files.delete(uri.toString()); }
			rename() {}
			watch() { return new vscode.Disposable(); }
		}

		exports.activate = async context => {
			const fs = new MemoryFs();
			context.subscriptions.push(vscode.workspace.registerFileSystemProvider("probe", fs));

			const left = vscode.Uri.from({ scheme: "probe", path: "/left.txt" });
			const right = vscode.Uri.from({ scheme: "probe", path: "/right.txt" });
			fs.writeFile(left, Buffer.from("a\nb\n"));
			fs.writeFile(right, Buffer.from("a\nB\n"));

			const results = {};
			const document = await vscode.workspace.openTextDocument(right);
			results.text = document.getText();
			results.lineCount = document.lineCount;
			results.line1 = document.lineAt(1).text;
			results.offset = document.offsetAt(new vscode.Position(1, 1));
			results.uriRoundTrip = vscode.Uri.parse(right.toString()).toString() === right.toString();
			results.fsPath = vscode.Uri.file("C:\\Users\\x\\a.txt").fsPath;
			results.joined = vscode.Uri.joinPath(vscode.Uri.file("C:\\a"), "b", "..", "c.txt").path;

			const edit = new vscode.WorkspaceEdit();
			edit.replace(right, new vscode.Range(0, 0, 0, 1), "Z");
			results.applied = await vscode.workspace.applyEdit(edit);
			results.afterEdit = document.getText();

			await vscode.commands.executeCommand("vscode.diff", left, right, "Probe diff");
			results.extensionFound = vscode.extensions.getExtension("forge.shim-probe")?.id;
			results.relative = vscode.workspace.asRelativePath(vscode.Uri.file(vscode.workspace.workspaceFolders[0].uri.fsPath + "\\sub\\file.cs"));
			results.version = vscode.version;

			await vscode.commands.executeCommand("probe.report", results);
		};
		""";

	[Fact]
	public async Task ShimSupportsVirtualDocumentsEditsAndDiffs() {
		string root = Path.Combine(Path.GetTempPath(), "forge-shim-probe", Guid.NewGuid().ToString("N"));
		string extension = Path.Combine(root, "probe");
		Directory.CreateDirectory(extension);
		await File.WriteAllTextAsync(Path.Combine(extension, "package.json"), Manifest, TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(Path.Combine(extension, "extension.js"), Extension, TestContext.Current.CancellationToken);

		using NodeExtensionHost host = new(Path.Combine(root, "storage"));

		ConcurrentQueue<string> log = [];
		ConcurrentQueue<(string Method, JsonNode? Parameters)> requests = [];
		TaskCompletionSource<JsonNode?> report = new(TaskCreationOptions.RunContinuationsAsynchronously);

		host.Log = log.Enqueue;
		host.AsyncRequestHandler = (method, parameters) => {
			requests.Enqueue((method, parameters?.DeepClone()));

			if (method == "commands/execute" && parameters?["id"]?.GetValue<string>() == "probe.report") report.TrySetResult(parameters["args"]?[0]?.DeepClone());

			return Task.FromResult<JsonNode?>(method switch {
				"window/openDiff" => true,
				_ => null,
			});
		};

		Assert.True(host.Start(new HostState { WorkspacePath = Directory.GetCurrentDirectory() }), string.Join("\n", log));

		await host.ActivateAsync(extension).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		JsonNode? results = await report.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

		Assert.NotNull(results);
		Assert.Equal("a\nB\n", results!["text"]?.GetValue<string>());
		Assert.Equal(3, results["lineCount"]?.GetValue<int>());
		Assert.Equal("B", results["line1"]?.GetValue<string>());
		Assert.Equal(3, results["offset"]?.GetValue<int>());
		Assert.True(results["uriRoundTrip"]?.GetValue<bool>());
		Assert.Equal("C:\\Users\\x\\a.txt", results["fsPath"]?.GetValue<string>());
		Assert.Equal("/C:/a/c.txt", results["joined"]?.GetValue<string>());
		Assert.True(results["applied"]?.GetValue<bool>());
		Assert.Equal("Z\nB\n", results["afterEdit"]?.GetValue<string>());
		Assert.Equal("forge.shim-probe", results["extensionFound"]?.GetValue<string>());
		Assert.Equal("sub/file.cs", results["relative"]?.GetValue<string>());

		(string _, JsonNode? diff) = Assert.Single(requests, entry => entry.Method == "window/openDiff");
		Assert.Equal("a\nb\n", diff?["originalText"]?.GetValue<string>());
		Assert.Equal("Z\nB\n", diff?["modifiedText"]?.GetValue<string>());
		Assert.Equal("probe", diff?["modified"]?["scheme"]?.GetValue<string>());
		Assert.Equal("Probe diff", diff?["title"]?.GetValue<string>());
		Assert.True(diff?["modifiedWritable"]?.GetValue<bool>());
	}
}
