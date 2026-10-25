using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Dotnet.Server;
using Forge.Extensions.Dotnet.Solution;

namespace Forge.Extensions.Dotnet.Language;

public sealed class EditorCommands(RoslynLanguageClient server, DotnetOutput output, DotnetSettings settings)
{
	public void Register(ICommandRegistry commands) {
		commands.Register(new Command("dotnet.formatDocument", "Format Document", ".NET", Format, "Shift+Alt+F"));
	}

	private void Format(ICommandHost host) {
		if (host.GetActiveFilePath() is not { } path || !ProjectDiscovery.IsSource(path)) return;

		_ = Task.Run(async () => {
			IReadOnlyList<ServerTextEdit> edits = await server.Formatting.DocumentAsync(path, settings.TabSize, settings.InsertSpaces).ConfigureAwait(false);

			if (edits.Count == 0) {
				output.Log(host, "Format Document: nothing to change.");
				return;
			}

			IReadOnlyList<ServerTextEdit> filtered = settings.PreserveCommentIndentation && host.GetDocumentText(path) is { } text ? CommentIndentFilter.Apply(text, edits) : edits;

			await host.ApplyWorkspaceEditAsync(new WorkspaceEdit([new WorkspaceOperation(WorkspaceOperationKind.Edit, path, [.. filtered.Select(static edit => new HostTextEdit(edit.StartLine, edit.StartColumn, edit.EndLine, edit.EndColumn, edit.NewText))])]) { IsRefactoring = false }).ConfigureAwait(false);
		});
	}
}
