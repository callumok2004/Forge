using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.Debug;

public sealed record TaskDefinition(string Label, string Type, string? Command)
{
	public string[] Args { get; init; } = [];

	public string? Cwd { get; init; }

	public string? Group { get; init; }

	public bool IsDefault { get; init; }

	public string[] DependsOn { get; init; } = [];

	public bool DependsInOrder { get; init; }

	public string CommandLine {
		get {
			if (Command is not { Length: > 0 } command) return string.Empty;

			return Args.Length == 0 ? command : $"{command} {string.Join(' ', Args)}";
		}
	}
}

public sealed class TaskConfigurations
{
	private readonly List<TaskDefinition> _tasks = [];

	public IReadOnlyList<TaskDefinition> Tasks => _tasks;

	public static string FileFor(string workspaceFolder) =>
		Path.Combine(workspaceFolder, ".vscode", "tasks.json");

	public static TaskConfigurations Load(string workspaceFolder) {
		TaskConfigurations configurations = new();
		string path = FileFor(workspaceFolder);

		if (!File.Exists(path)) return configurations;

		JsonNode? root;

		try {
			root = JsonNode.Parse(File.ReadAllText(path), null, new JsonDocumentOptions {
				AllowTrailingCommas = true,
				CommentHandling = JsonCommentHandling.Skip,
			});
		}
		catch (Exception exception) when (exception is JsonException or IOException
			or UnauthorizedAccessException) {
			return configurations;
		}

		if (root?["tasks"] is not JsonArray tasks) return configurations;

		foreach (JsonNode? entry in tasks) {
			if (entry is JsonObject o && Read(o) is { } task) configurations._tasks.Add(task);
		}

		return configurations;
	}

	public static TaskDefinition Resolve(TaskDefinition task, ResolverContext context) {
		string[] args = new string[task.Args.Length];

		for (int i = 0; i < args.Length; i++) args[i] = VariableResolver.Resolve(task.Args[i], context);

		return task with {
			Command = task.Command is { Length: > 0 } command
				? VariableResolver.Resolve(command, context)
				: task.Command,
			Args = args,
			Cwd = task.Cwd is { Length: > 0 } cwd ? VariableResolver.Resolve(cwd, context) : task.Cwd,
		};
	}

	public TaskDefinition? Find(string label) {
		foreach (TaskDefinition task in _tasks) {
			if (string.Equals(task.Label, label, StringComparison.Ordinal)) return task;
		}

		return null;
	}

	public TaskDefinition? DefaultFor(string group) {
		foreach (TaskDefinition task in _tasks) {
			if (task.IsDefault && string.Equals(task.Group, group, StringComparison.Ordinal)) return task;
		}

		foreach (TaskDefinition task in _tasks) {
			if (string.Equals(task.Group, group, StringComparison.Ordinal)) return task;
		}

		return null;
	}

	public IReadOnlyList<TaskDefinition> Chain(TaskDefinition task) {
		List<TaskDefinition> ordered = [];
		HashSet<string> seen = new(StringComparer.Ordinal);

		Visit(task);
		return ordered;

		void Visit(TaskDefinition current) {
			if (!seen.Add(current.Label)) return;

			foreach (string label in current.DependsOn) {
				if (Find(label) is { } dependency) Visit(dependency);
			}

			ordered.Add(current);
		}
	}

	private static TaskDefinition? Read(JsonObject o) {
		string? label = o["label"]?.GetValue<string>() ?? o["taskName"]?.GetValue<string>();

		if (label is null) return null;

		string? group = null;
		bool isDefault = false;

		switch (o["group"]) {
			case JsonValue value when value.TryGetValue(out string? name):
				group = name;
				break;

			case JsonObject nested:
				group = nested["kind"]?.GetValue<string>();
				isDefault = nested["isDefault"]?.GetValue<bool>() ?? false;
				break;
		}

		List<string> args = [];

		if (o["args"] is JsonArray argsArray) {
			foreach (JsonNode? entry in argsArray) {
				string? value = entry is JsonObject nested
					? nested["value"]?.GetValue<string>()
					: entry?.GetValue<string>();

				if (value is { Length: > 0 }) args.Add(value);
			}
		}

		List<string> dependsOn = [];

		switch (o["dependsOn"]) {
			case JsonValue value when value.TryGetValue(out string? single):
				dependsOn.Add(single);
				break;

			case JsonArray array:
				foreach (JsonNode? entry in array) {
					if (entry?.GetValue<string>() is { } value) dependsOn.Add(value);
				}

				break;
		}

		return new TaskDefinition(label, o["type"]?.GetValue<string>() ?? "shell",
			o["command"]?.GetValue<string>()) {
			Args = [.. args],
			Cwd = o["options"]?["cwd"]?.GetValue<string>(),
			Group = group,
			IsDefault = isDefault,
			DependsOn = [.. dependsOn],
			DependsInOrder = o["dependsOrder"]?.GetValue<string>() == "sequence",
		};
	}
}
