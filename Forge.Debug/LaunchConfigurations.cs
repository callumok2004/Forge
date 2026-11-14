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
		Forge.Core.WorkspaceConfig.PathFor(workspaceFolder, "launch.json");

	private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

	public static LaunchConfigurations Load(string workspaceFolder) {
		LaunchConfigurations launch = new() { Path = FileFor(workspaceFolder) };
		Dictionary<string, List<LaunchOption>> extra = new(StringComparer.Ordinal);

		foreach (string layer in Forge.Core.WorkspaceConfig.Layers(workspaceFolder, "launch.json")) {
			JsonNode? root;

			try {
				root = JsonNode.Parse(File.ReadAllText(layer), null, ParseOptions);
			}
			catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) {
				continue;
			}

			if (root?["configurations"] is JsonArray configurations) {
				foreach (JsonNode? entry in configurations) {
					if (entry is not JsonObject o || Read(o) is not { } configuration) continue;

					configuration = configuration with { Options = ReadOptions(o["options"], layer, inline: true) };
					int existing = launch._configurations.FindIndex(candidate => candidate.Name == configuration.Name);

					if (existing >= 0) launch._configurations[existing] = configuration;
					else launch._configurations.Add(configuration);
				}
			}

			if (root?["compounds"] is JsonArray compounds) {
				foreach (JsonNode? entry in compounds) {
					if (entry?["name"]?.GetValue<string>() is not { } name) continue;

					List<string> names = [];

					if (entry["configurations"] is JsonArray members) {
						foreach (JsonNode? member in members) {
							string? value = member is JsonObject nested ? nested["name"]?.GetValue<string>() : member?.GetValue<string>();

							if (value is { Length: > 0 }) names.Add(value);
						}
					}

					launch._compounds.RemoveAll(candidate => candidate.Name == name);
					launch._compounds.Add(new DebugCompound(name, names, entry["preLaunchTask"]?.GetValue<string>()));
				}
			}

			if (root?["options"] is JsonObject options) {
				foreach ((string name, JsonNode? list) in options) {
					if (!extra.TryGetValue(name, out List<LaunchOption>? target)) extra[name] = target = [];

					target.AddRange(ReadOptions(list, layer, inline: false));
				}
			}
		}

		for (int i = 0; i < launch._configurations.Count; i++) {
			DebugConfiguration configuration = launch._configurations[i];

			if (extra.TryGetValue(configuration.Name, out List<LaunchOption>? added)) launch._configurations[i] = configuration with { Options = [.. configuration.Options, .. added] };
		}

		return launch;
	}

	private static List<LaunchOption> ReadOptions(JsonNode? node, string source, bool inline) {
		List<LaunchOption> options = [];

		if (node is not JsonArray array) return options;

		for (int i = 0; i < array.Count; i++) {
			if (array[i] is not JsonObject option) continue;

			List<string> args = [];

			if (option["args"] is JsonArray list) {
				foreach (JsonNode? arg in list) {
					if (arg is JsonValue value) args.Add(value.ToString());
				}
			}
			else if (option["args"] is JsonValue single) args.Add(single.ToString());

			string label = option["label"]?.GetValue<string>() ?? string.Join(' ', args);
			string? optionValue = option["value"] is JsonValue raw ? raw.ToString() : null;
			bool enabled = option["enabled"] is JsonValue flag && flag.TryGetValue(out bool on) && on;

			options.Add(new LaunchOption(label, args, optionValue, enabled, source, inline, i));
		}

		return options;
	}

	public static List<string> SplitArguments(string text) {
		List<string> args = [];
		System.Text.StringBuilder current = new();
		bool quoted = false;
		bool any = false;

		foreach (char c in text) {
			if (c == '"') {
				quoted = !quoted;
				any = true;
			}
			else if (char.IsWhiteSpace(c) && !quoted) {
				if (any) args.Add(current.ToString());

				current.Clear();
				any = false;
			}
			else {
				current.Append(c);
				any = true;
			}
		}

		if (any) args.Add(current.ToString());

		return args;
	}

	public static string JoinArguments(IEnumerable<string> args) => string.Join(' ', args.Select(static arg => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg));

	private static JsonObject? ReadRoot(string path) {
		if (!File.Exists(path)) return [];

		return JsonNode.Parse(File.ReadAllText(path), null, ParseOptions) as JsonObject;
	}

	private static void Save(string path, JsonObject root) {
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		string temporary = path + ".tmp";
		File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));
		File.Move(temporary, path, overwrite: true);
	}

	public static bool AddOption(string workspaceFolder, string configuration, string label, IReadOnlyList<string> args) {
		string path = System.IO.Path.Combine(workspaceFolder, Forge.Core.WorkspaceConfig.ForgeFolder, "launch.json");

		try {
			if (ReadRoot(path) is not { } root) return false;

			if (root["options"] is not JsonObject options) root["options"] = options = [];

			if (options[configuration] is not JsonArray list) options[configuration] = list = [];

			list.Add(new JsonObject {
				["label"] = label,
				["args"] = new JsonArray([.. args.Select(static arg => (JsonNode?)JsonValue.Create(arg))]),
				["enabled"] = true,
			});

			Save(path, root);
			return true;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	public static bool RemoveOption(string configuration, LaunchOption option) {
		try {
			if (ReadRoot(option.Source) is not { } root || Locate(root, configuration, option) is not JsonArray list) return false;

			list.RemoveAt(option.Index);
			Save(option.Source, root);
			return true;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	private static JsonArray? Locate(JsonObject root, string configuration, LaunchOption option) {
		JsonNode? list = null;

		if (option.Inline && root["configurations"] is JsonArray configurations) {
			foreach (JsonNode? entry in configurations) {
				if (entry?["name"]?.GetValue<string>() == configuration) list = entry["options"];
			}
		}
		else if (!option.Inline) list = root["options"]?[configuration];

		return list is JsonArray array && option.Index < array.Count ? array : null;
	}

	public static bool Write(string configuration, LaunchOption option, bool enabled, string? value) {
		try {
			if (JsonNode.Parse(File.ReadAllText(option.Source), null, ParseOptions) is not JsonObject root) return false;

			JsonNode? list = null;

			if (option.Inline && root["configurations"] is JsonArray configurations) {
				foreach (JsonNode? entry in configurations) {
					if (entry?["name"]?.GetValue<string>() == configuration) list = entry["options"];
				}
			}
			else if (!option.Inline) list = root["options"]?[configuration];

			if (list is not JsonArray array || option.Index >= array.Count || array[option.Index] is not JsonObject target) return false;

			target["enabled"] = enabled;

			if (value is not null) target["value"] = value;

			string temporary = option.Source + ".tmp";
			File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1 }));
			File.Move(temporary, option.Source, overwrite: true);
			return true;
		}
		catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) {
			return false;
		}
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

		string[]? args = ReadArguments(raw?["args"]) ?? configuration.Args;
		List<string> extra = [];

		foreach (LaunchOption option in configuration.Options) {
			if (!option.Enabled) continue;

			foreach (string arg in option.Resolve()) extra.Add(VariableResolver.Resolve(arg, context));
		}

		if (extra.Count > 0) {
			args = [.. args ?? [], .. extra];

			if (raw is not null) raw["args"] = new JsonArray([.. args.Select(static arg => (JsonNode?)JsonValue.Create(arg))]);
		}

		raw?.Remove("options");

		return configuration with {
			Program = raw?["program"]?.GetValue<string>() ?? Resolve(configuration.Program, context),
			Cwd = raw?["cwd"]?.GetValue<string>() ?? Resolve(configuration.Cwd, context) ?? context.WorkspaceFolder,
			Args = args,
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
