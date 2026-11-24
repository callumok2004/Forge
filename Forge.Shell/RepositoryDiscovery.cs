using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Core;
using Forge.Git;

namespace Forge.Shell;

public static class RepositoryDiscovery
{
	public static GitDiscoveryOptions Options(Settings settings) {
		GitDiscoveryOptions defaults = new();

		return new GitDiscoveryOptions {
			AutoRepositoryDetection = settings.GetRaw("git.autoRepositoryDetection") switch {
				JsonValue value when value.TryGetValue(out bool flag) => flag ? "true" : "false",
				JsonValue value when value.TryGetValue(out string? text) && text is "subFolders" or "openEditors" => text,
				_ => defaults.AutoRepositoryDetection,
			},
			RepositoryScanMaxDepth = settings.GetInt32("git.repositoryScanMaxDepth") ?? defaults.RepositoryScanMaxDepth,
			RepositoryScanIgnoredFolders = settings.GetStringArray("git.repositoryScanIgnoredFolders") ?? defaults.RepositoryScanIgnoredFolders,
			ScanRepositories = settings.GetStringArray("git.scanRepositories") ?? defaults.ScanRepositories,
			DetectSubmodules = settings.GetBoolean("git.detectSubmodules") ?? defaults.DetectSubmodules,
			DetectSubmodulesLimit = settings.GetInt32("git.detectSubmodulesLimit") ?? defaults.DetectSubmodulesLimit,
			IgnoredRepositories = settings.GetStringArray("git.ignoredRepositories") ?? defaults.IgnoredRepositories,
			OpenRepositoryInParentFolders = settings.GetString("git.openRepositoryInParentFolders") is "always" or "never" or "prompt" ? settings.GetString("git.openRepositoryInParentFolders")! : defaults.OpenRepositoryInParentFolders,
		};
	}

	public static HashSet<string> ReadSet(string path) {
		HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);

		try {
			if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray array) {
				foreach (JsonNode? node in array) {
					if (node?.GetValue<string>() is { Length: > 0 } value) set.Add(value);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
		}

		return set;
	}

	public static void WriteSet(string path, IEnumerable<string> values) {
		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, new JsonArray([.. values.Select(static value => (JsonNode)value)]).ToJsonString());
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	public static void Notify(Workbench workbench, GitRepositories repositories, string parentsPath, Action refresh) {
		IReadOnlyList<string> parents = repositories.ParentRepositories;

		if (parents.Count > 0 && Settings.Current.GetString("git.openRepositoryInParentFolders") is null or "prompt") {
			string message = parents.Count == 1 ? "A git repository was found in the parent folders of the workspace or the open file(s). Would you like to open the repository?" : "Git repositories were found in the parent folders of the workspace or the open file(s). Would you like to open the repositories?";

			workbench.Notifications.Show(MessageSeverity.Info, message, ["Yes", "Always", "Never"], choice => {
				if (choice == 0) PickParents(workbench, repositories, parentsPath, refresh);
				else if (choice is 1 or 2) {
					Settings.Current.SetValue("git.openRepositoryInParentFolders", choice == 1 ? "always" : "never");

					if (choice == 1) {
						foreach (string parent in repositories.ParentRepositories) OpenParent(repositories, parentsPath, parent);
					}

					refresh();
				}
			});
		}

		foreach ((string repository, int count) in repositories.TooManySubmodules) workbench.Notifications.Show(MessageSeverity.Warning, $"The \"{Path.GetFileName(repository)}\" repository has {count} submodules which won't be opened automatically. You can still open each one individually by opening a file within.", [], null);
	}

	private static void PickParents(Workbench workbench, GitRepositories repositories, string parentsPath, Action refresh) {
		List<string> parents = [.. repositories.ParentRepositories.OrderBy(static parent => Path.GetFileName(parent), StringComparer.OrdinalIgnoreCase)];
		List<QuickPickItem> items = [.. parents.Select(static parent => new QuickPickItem("$(repo) " + Path.GetFileName(parent), parent))];

		if (parents.Count > 1) items.Add(new QuickPickItem("All Repositories"));

		workbench.QuickInput.Show(new Forge.Commands.ListQuickPick("Pick a repository to open", items, index => {
			if (index < 0) return;

			foreach (string parent in index < parents.Count ? [parents[index]] : parents) OpenParent(repositories, parentsPath, parent);

			refresh();
		}));
	}

	private static void OpenParent(GitRepositories repositories, string parentsPath, string parent) {
		HashSet<string> trusted = ReadSet(parentsPath);

		if (trusted.Add(parent)) WriteSet(parentsPath, trusted);

		ThreadPool.UnsafeQueueUserWorkItem(_ => repositories.Open(parent, openIfParent: true), null);
	}
}
