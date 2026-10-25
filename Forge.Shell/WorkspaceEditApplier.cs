using Forge.Common.Commands;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class WorkspaceEditApplier(Workbench workbench)
{
	public bool Apply(WorkspaceEdit edit) {
		bool all = true;

		foreach (WorkspaceOperation operation in edit.Operations) {
			all &= operation.Kind switch {
				WorkspaceOperationKind.Edit => ApplyEdits(operation.Path, operation.Edits, edit.IsRefactoring),
				WorkspaceOperationKind.Create => Create(operation),
				WorkspaceOperationKind.Rename => Rename(operation),
				WorkspaceOperationKind.Delete => Delete(operation),
				_ => false,
			};
		}

		return all;
	}

	private bool ApplyEdits(string path, IReadOnlyList<HostTextEdit> edits, bool save) {
		if (edits.Count == 0) return true;

		int index = workbench.IndexOf(path);

		if (index >= 0) {
			Document document = workbench.Documents[index];
			bool wasClean = !document.IsDirty();
			DocumentEdits.Apply(document.Buffer, edits, out int earliest, out int delta);

			if (earliest != int.MaxValue) workbench.ViewAt(index).ExternalEdit(earliest, delta);

			workbench.Publish(document);

			return !save || !wasClean || document.Save();
		}

		try {
			Document document = Document.Open(path);

			DocumentEdits.Apply(document.Buffer, edits, out _, out _);
			return document.Save();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	private static bool Create(WorkspaceOperation operation) {
		try {
			if (File.Exists(operation.Path)) {
				if (operation.IgnoreIfExists) return true;

				if (!operation.Overwrite) return false;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(operation.Path)!);
			File.WriteAllBytes(operation.Path, []);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	private bool Rename(WorkspaceOperation operation) {
		if (operation.NewPath is not { } target) return false;

		try {
			if (File.Exists(target)) {
				if (operation.IgnoreIfExists) return true;

				if (!operation.Overwrite) return false;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(target)!);

			if (Directory.Exists(operation.Path)) Directory.Move(operation.Path, target);
			else File.Move(operation.Path, target, operation.Overwrite);

			int index = workbench.IndexOf(operation.Path);

			if (index >= 0) {
				workbench.Close(index);
				workbench.OpenFile(target);
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	private bool Delete(WorkspaceOperation operation) {
		try {
			int index = workbench.IndexOf(operation.Path);

			if (index >= 0) workbench.Close(index);

			if (Directory.Exists(operation.Path)) Directory.Delete(operation.Path, operation.Recursive);
			else if (File.Exists(operation.Path)) File.Delete(operation.Path);
			else return operation.IgnoreIfNotExists;

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}
	}
}
