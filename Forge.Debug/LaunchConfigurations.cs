using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Common.Debug;

namespace Forge.Debug;

public sealed class LaunchConfigurations
{
	private readonly List<DebugConfiguration> _configurations = [];
	private readonly List<DebugCompound> _compounds = [];

	public string? Path { get; private set; }

	public IReadOnlyList<DebugConfiguration> Configurations => _configurations;

	public IReadOnlyList<DebugCompound> Compounds => _compounds;

	public bool IsEmpty => _configurations.Count == 0 && _compounds.Count == 0;

	public static string FileFor(string workspaceFolder) =>
		System.IO.Path.Combine(workspaceFolder, ".vscode", "launch.json");

	public static LaunchConfigurations Load(string workspaceFolder) {
		LaunchConfigurations launch = new();
		string path = FileFor(workspaceFolder);

		launch.Path = path;

		if (!File.Exists(path)) return launch;

		JsonNode? root;

		try {
			root = JsonNode.Parse(File.ReadAllText(path), null,
				new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
		}
		catch (Exception exception) when (exception is JsonException or IOException
			or UnauthorizedAccessException) {
			return launch;
		}

		if (root?["configurations"] is JsonArray configurations) {
			foreach (JsonNode? entry in configurations) {
				if (entry is JsonObject o && Read(o) is { } configuration) launch._configurations.Add(configuration);
			}
		}

		if (root?["compounds"] is JsonArray compounds) {
			foreach (JsonNode? entry in compounds) {
				if (entry?["name"]?.GetValue<string>() is not { } name) continue;

				List<string> names = [];

				if (entry["configurations"] is JsonArray members) {
					foreach (JsonNode? member in members) {
						string? value = member is JsonObject nested
							? nested["name"]?.GetValue<string>()
							: member?.GetValue<string>();

						if (value is { Length: > 0 }) names.Add(value);
					}
				}

				launch._compounds.Add(new DebugCompound(name, names,
					entry["preLaunchTask"]?.GetValue<string>()));
			}
		}

		return launch;
	}

	public DebugConfiguration? Find(string name) {
		foreach (DebugConfiguration configuration in _configurations) {
			if (string.Equals(configuration.Name, name, StringComparison.Ordinal)) return configuration;
		}

		return null;
	}

	public DebugCompound? FindCompound(string name) {
		foreach (DebugCompound compound in _compounds) {
			if (string.Equals(compound.Name, name, StringComparison.Ordinal)) return compound;
		}

		return null;
	}

	public static DebugConfiguration Resolve(DebugConfiguration configuration, ResolverContext context) {
		JsonObject? raw = configuration.Raw is { } source
			? VariableResolver.Resolve(source, context) as JsonObject
			: null;

		Dictionary<string, string> environment = new(StringComparer.Ordinal);

		if (raw?["env"] is JsonObject env) {
			foreach (KeyValuePair<string, JsonNode?> pair in env) {
				environment[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
			}
		}

		return configuration with {
			Program = raw?["program"]?.GetValue<string>() ?? Resolve(configuration.Program, context),
			Cwd = raw?["cwd"]?.GetValue<string>() ?? Resolve(configuration.Cwd, context) ?? context.WorkspaceFolder,
			Args = ReadArguments(raw?["args"]) ?? configuration.Args,
			Environment = environment,
			Raw = raw ?? configuration.Raw,
		};
	}

	private static string? Resolve(string? value, ResolverContext context) =>
		value is { Length: > 0 } ? VariableResolver.Resolve(value, context) : value;

	private static string[]? ReadArguments(JsonNode? node) {
		if (node is not JsonArray array) return null;

		List<string> values = [];

		foreach (JsonNode? entry in array) {
			if (entry?.GetValue<string>() is { } value) values.Add(value);
		}

		return [.. values];
	}

	private static DebugConfiguration? Read(JsonObject o) {
		if (o["name"]?.GetValue<string>() is not { } name) return null;
		if (o["type"]?.GetValue<string>() is not { } type) return null;

		Dictionary<string, string> environment = new(StringComparer.Ordinal);

		if (o["env"] is JsonObject env) {
			foreach (KeyValuePair<string, JsonNode?> pair in env) {
				environment[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
			}
		}

		return new DebugConfiguration(name, type, o["request"]?.GetValue<string>() ?? "launch") {
			Program = o["program"]?.GetValue<string>(),
			Cwd = o["cwd"]?.GetValue<string>(),
			Args = ReadArguments(o["args"]),
			Console = o["console"]?.GetValue<string>(),
			PreLaunchTask = o["preLaunchTask"]?.GetValue<string>(),
			PostDebugTask = o["postDebugTask"]?.GetValue<string>(),
			StopAtEntry = o["stopAtEntry"]?.GetValue<bool>() ?? false,
			Environment = environment,
			Raw = (JsonObject)o.DeepClone(),
		};
	}
}
