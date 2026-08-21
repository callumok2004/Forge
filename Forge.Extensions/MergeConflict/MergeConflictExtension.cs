using Forge.Commands;
using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.Host;

namespace Forge.Extensions.MergeConflict;

public sealed class MergeConflictExtension : ForgeExtension
{
	public override string Identifier => "forge.merge-conflict";

	public override string DisplayName => "Merge Conflict";

	public override ActivationEvent Activation => ActivationEvent.Startup;

	protected override string CommandPrefix => "merge-conflict.";

	public override void Register(ICommandRegistry commands) {
		commands.Register(new Command("merge-conflict.accept.current", "Accept Current Change", "Merge Conflict",
			host => AcceptFirst(host, MergeResolution.Current)));

		commands.Register(new Command("merge-conflict.accept.incoming", "Accept Incoming Change", "Merge Conflict",
			host => AcceptFirst(host, MergeResolution.Incoming)));

		commands.Register(new Command("merge-conflict.accept.both", "Accept Both Changes", "Merge Conflict",
			host => AcceptFirst(host, MergeResolution.Both)));

		commands.Register(new Command("merge-conflict.accept.all-current", "Accept All Current", "Merge Conflict",
			host => AcceptAll(host, MergeResolution.Current)));

		commands.Register(new Command("merge-conflict.accept.all-incoming", "Accept All Incoming", "Merge Conflict",
			host => AcceptAll(host, MergeResolution.Incoming)));

		commands.Register(new Command("merge-conflict.accept.all-both", "Accept All Both", "Merge Conflict",
			host => AcceptAll(host, MergeResolution.Both)));

		commands.Register(new Command("merge-conflict.next", "Next Conflict", "Merge Conflict",
			host => Jump(host, forward: true)));

		commands.Register(new Command("merge-conflict.previous", "Previous Conflict", "Merge Conflict",
			host => Jump(host, forward: false)));
	}

	private static void AcceptFirst(ICommandHost host, MergeResolution resolution) {
		int resolved = 0;

		host.EditActiveDocument(text => {
			List<MergeConflict> conflicts = MergeConflictScanner.Find(text);

			if (conflicts.Count == 0) return null;

			resolved = 1;
			return MergeConflictScanner.Resolve(text, Nearest(conflicts, host.GetCaretLine() - 1), resolution);
		});

		host.Log(resolved == 0 ? "No merge conflicts in this file." : "Conflict resolved.");
	}

	private static void AcceptAll(ICommandHost host, MergeResolution resolution) {
		int resolved = 0;

		host.EditActiveDocument(text => {
			resolved = MergeConflictScanner.Find(text).Count;

			return resolved == 0 ? null : MergeConflictScanner.ResolveAll(text, resolution);
		});

		host.Log(resolved == 0 ? "No merge conflicts in this file." : $"{resolved} conflict(s) resolved.");
	}

	private static void Jump(ICommandHost host, bool forward) {
		if (host.GetActiveFilePath() is not { } path) return;

		List<MergeConflict> conflicts = [];

		host.EditActiveDocument(text => {
			conflicts = MergeConflictScanner.Find(text);
			return null;
		});

		if (conflicts.Count == 0) {
			host.Log("No merge conflicts in this file.");
			return;
		}

		int caret = host.GetCaretLine() - 1;

		MergeConflict target = forward
			? conflicts.FirstOrDefault(c => c.CurrentStart > caret, conflicts[0])
			: conflicts.LastOrDefault(c => c.CurrentStart < caret, conflicts[^1]);

		host.GoTo(path, target.CurrentStart + 1, 1);
	}

	private static MergeConflict Nearest(List<MergeConflict> conflicts, int caretLine) {
		foreach (MergeConflict conflict in conflicts) {
			if (caretLine >= conflict.CurrentStart && caretLine <= conflict.IncomingEnd + 1) return conflict;
		}

		return conflicts[0];
	}
}
