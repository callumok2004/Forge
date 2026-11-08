using System.Text;
using System.Text.Json.Nodes;

using Forge.Common.Commands;
using Forge.Core;
using Forge.Debug;

namespace Forge.Shell;

public sealed class JsonSchemaFeatures(Func<string, string?> textOf, Func<string?> workspaceRoot) : ILanguageFeatures
{
	private enum FileKind : byte
	{
		None,
		Settings,
		Launch,
		Tasks,
	}

	private readonly record struct Property(string Key, string Description, string Snippet, string[]? Values = null, string[]? ValueDescriptions = null);

	private readonly record struct Context(List<string> Path, bool IsKey, int StringStart, string? Key, HashSet<string> Present, bool AfterColon);

	private static readonly LanguageTriggers Triggers = new("\":", string.Empty, string.Empty, string.Empty, string.Empty);

	public LanguageTriggers GetTriggers() => Triggers;

	public bool Handles(string path) => KindOf(path) != FileKind.None;

	private FileKind KindOf(string path) {
		ReadOnlySpan<char> name = Path.GetFileName(path.AsSpan());

		if (string.Equals(path, Settings.Current.UserPath, StringComparison.OrdinalIgnoreCase)) return FileKind.Settings;

		ReadOnlySpan<char> folder = Path.GetFileName(Path.GetDirectoryName(path.AsSpan()));

		if (!folder.Equals(WorkspaceConfig.VSCodeFolder, StringComparison.OrdinalIgnoreCase) && !folder.Equals(WorkspaceConfig.ForgeFolder, StringComparison.OrdinalIgnoreCase)) return FileKind.None;

		if (name.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) return FileKind.Settings;

		if (name.Equals("launch.json", StringComparison.OrdinalIgnoreCase)) return FileKind.Launch;

		return name.Equals("tasks.json", StringComparison.OrdinalIgnoreCase) ? FileKind.Tasks : FileKind.None;
	}

	public Task<CompletionList> CompletionsAsync(string path, int line, int column, char trigger, CompletionTrigger kind) {
		FileKind file = KindOf(path);

		if (file == FileKind.None || textOf(path) is not { } text) return Task.FromResult(new CompletionList([], false));

		int offset = OffsetOf(text, line - 1, column - 1);

		if (Scan(text, offset) is not { } context) return Task.FromResult(new CompletionList([], false));

		int lineStart = offset - (column - 1);
		int replaceStart = context.StringStart >= 0 ? context.StringStart - lineStart + 1 : WordStart(text, lineStart, offset) - lineStart + 1;
		bool closingQuote = context.StringStart >= 0 && offset < text.Length && text[offset] == '"';
		List<CompletionEntry> items = [];

		if (context.IsKey) {
			foreach (Property property in KeysFor(file, context.Path)) {
				if (context.Present.Contains(property.Key)) continue;

				string insert = $"\"{property.Key}\": {property.Snippet}";
				items.Add(Entry(items.Count, property.Key, insert, property.Description, "Property", "\"" + property.Key + "\"", line, replaceStart, column, closingQuote));
			}
		}
		else if (context.Key is { } key && ValuesFor(file, context.Path, key) is { } values) {
			foreach ((string value, string description) in values) {
				bool quoted = value.StartsWith('"');

				if (context.StringStart >= 0 && !quoted) continue;

				items.Add(Entry(items.Count, value.Trim('"'), value, description, "Value", value, line, replaceStart, column, closingQuote));
			}
		}

		return Task.FromResult(new CompletionList(items, false));
	}

	private static CompletionEntry Entry(int index, string label, string insert, string detail, string kind, string filter, int line, int start, int column, bool closingQuote) =>
		new(index, label, string.Empty, string.Empty, insert, detail, kind, filter, false, line, start, column) { ReplacesToEnd = closingQuote };

	private static int WordStart(string text, int lineStart, int offset) {
		int start = offset;

		while (start > lineStart && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.' or '-' or '$')) start--;

		return start;
	}

	private static int OffsetOf(string text, int line, int column) {
		int offset = 0;

		for (int current = 0; current < line && offset < text.Length; current++) {
			int next = text.IndexOf('\n', offset);

			if (next < 0) return text.Length;

			offset = next + 1;
		}

		return Math.Min(text.Length, offset + column);
	}

	private sealed class Frame(bool isObject)
	{
		public readonly bool IsObject = isObject;
		public string? Key;
		public bool AfterColon;
		public readonly HashSet<string> Present = new(StringComparer.Ordinal);
	}

	private static Context? Scan(string text, int caret) {
		List<Frame> stack = [];
		List<string> path = [];
		int i = 0;

		while (i < caret) {
			char c = text[i];

			if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') {
				int end = text.IndexOf('\n', i);

				if (end < 0 || end >= caret) return null;

				i = end + 1;
				continue;
			}

			if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
				int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);

				if (end < 0 || end + 2 > caret) return null;

				i = end + 2;
				continue;
			}

			if (c == '"') {
				int start = i;
				StringBuilder value = new();
				i++;

				while (i < caret && text[i] != '"') {
					if (text[i] == '\\' && i + 1 < caret) {
						value.Append(text[i + 1]);
						i += 2;
						continue;
					}

					if (text[i] == '\n') break;

					value.Append(text[i]);
					i++;
				}

				if (i >= caret || text[i] == '\n') {
					if (stack.Count == 0) return null;

					Frame open = stack[^1];
					bool key = open.IsObject && !open.AfterColon;
					return new Context(path, key, start, key ? null : open.Key, open.Present, open.AfterColon);
				}

				i++;

				if (stack.Count > 0 && stack[^1] is { IsObject: true, AfterColon: false } frame) {
					frame.Key = value.ToString();
					frame.Present.Add(frame.Key);
				}

				continue;
			}

			switch (c) {
				case '{':
				case '[':
					if (stack.Count > 0) {
						Frame parent = stack[^1];
						path.Add(parent.IsObject ? parent.Key ?? string.Empty : "[]");
					}

					stack.Add(new Frame(c == '{'));
					break;
				case '}':
				case ']':
					if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);

					if (path.Count > 0 && stack.Count > 0) path.RemoveAt(path.Count - 1);

					if (stack.Count > 0 && stack[^1].IsObject) stack[^1].AfterColon = true;

					break;
				case ':':
					if (stack.Count > 0 && stack[^1].IsObject) stack[^1].AfterColon = true;

					break;
				case ',':
					if (stack.Count > 0 && stack[^1].IsObject) {
						stack[^1].AfterColon = false;
						stack[^1].Key = null;
					}

					break;
			}

			i++;
		}

		if (stack.Count == 0) return null;

		Frame top = stack[^1];

		if (!top.IsObject) return new Context(path, false, -1, path.Count > 0 ? path[^1] : null, top.Present, true);

		return new Context(path, !top.AfterColon, -1, top.AfterColon ? top.Key : null, top.Present, top.AfterColon);
	}

	private IEnumerable<Property> KeysFor(FileKind file, List<string> path) {
		switch (file) {
			case FileKind.Settings when path.Count == 0:
				foreach (SettingDefinition setting in SettingsSchema.All) yield return new Property(setting.Key, setting.Description, SnippetFor(setting.Types, setting.Default));

				break;
			case FileKind.Launch when path.Count == 0:
				yield return new Property("version", "Version of this file format.", "\"0.2.0\"");
				yield return new Property("configurations", "List of configurations. Add new configurations or edit existing ones by using IntelliSense.", "[$0]");
				yield return new Property("compounds", "List of compounds. Each compound references multiple configurations which will get launched together.", "[$0]");
				break;
			case FileKind.Launch when path is ["configurations", "[]"]:
				foreach (Property property in LaunchProperties) yield return property;

				break;
			case FileKind.Launch when path is ["compounds", "[]"]:
				yield return new Property("name", "Name of compound. Appears in the launch configuration drop down menu.", "\"$0\"");
				yield return new Property("configurations", "Names of configurations that will be started as part of this compound.", "[$0]");
				yield return new Property("preLaunchTask", "Task to run before any of the compound configurations start.", "\"$0\"");
				yield return new Property("stopAll", "Controls whether manually terminating one session will stop all of the compound sessions.", "false");
				break;
			case FileKind.Tasks when path.Count == 0:
				yield return new Property("version", "The config's version number.", "\"2.0.0\"");
				yield return new Property("tasks", "The task configurations. Usually these are enrichments of task already defined in the external task runner.", "[$0]");
				yield return new Property("inputs", "User inputs. Used for defining user input prompts, such as free string input or a choice from several options.", "[$0]");
				yield return new Property("options", "Additional command options", "{$0}");
				yield return new Property("presentation", "Configures the panel that is used to present the task's output and reads its input.", "{$0}");
				break;
			case FileKind.Tasks when path is ["tasks", "[]"]:
				foreach (Property property in TaskProperties) yield return property;

				break;
			case FileKind.Tasks when path is ["tasks", "[]", "presentation"] or ["presentation"]:
				foreach (Property property in PresentationProperties) yield return property;

				break;
			case FileKind.Tasks when path is ["tasks", "[]", "options"] or ["options"]:
				yield return new Property("cwd", "The current working directory of the executed program or script. If omitted Code's current workspace root is used.", "\"$0\"");
				yield return new Property("env", "The environment of the executed program or shell. If omitted the parent process' environment is used.", "{$0}");
				yield return new Property("shell", "Configures the shell to be used.", "{$0}");
				break;
			case FileKind.Tasks when path is ["tasks", "[]", "group"]:
				yield return new Property("kind", "The task's execution group.", "\"build\"", ["\"build\"", "\"test\"", "\"none\""]);
				yield return new Property("isDefault", "Defines if this task is the default task in the group, or a glob to match the file which should trigger this task.", "true");
				break;
		}
	}

	private IEnumerable<(string Value, string Description)>? ValuesFor(FileKind file, List<string> path, string key) {
		if (file == FileKind.Settings && path.Count == 0) {
			if (SettingsSchema.Find(key) is not { } setting) return null;

			return SettingValues(setting);
		}

		Property? property = file switch {
			FileKind.Launch when path is ["configurations", "[]"] => LaunchProperties.FirstOrDefault(entry => entry.Key == key),
			FileKind.Tasks when path is ["tasks", "[]"] => TaskProperties.FirstOrDefault(entry => entry.Key == key),
			FileKind.Tasks when path is ["tasks", "[]", "presentation"] or ["presentation"] => PresentationProperties.FirstOrDefault(entry => entry.Key == key),
			FileKind.Tasks when path is ["tasks", "[]", "group"] && key == "kind" => new Property("kind", string.Empty, string.Empty, ["\"build\"", "\"test\"", "\"none\""]),
			_ => null,
		};

		if (file == FileKind.Launch && key is "preLaunchTask" or "postDebugTask" && workspaceRoot() is { } root) return TaskLabels(root);

		if (file == FileKind.Tasks && key == "dependsOn" && workspaceRoot() is { } tasksRoot) return TaskLabels(tasksRoot);

		if (property is not { Key.Length: > 0, Values: { } values } found) return null;

		return values.Select((value, index) => (value, found.ValueDescriptions is { } descriptions && index < descriptions.Length ? descriptions[index] : string.Empty));
	}

	private static IEnumerable<(string, string)> TaskLabels(string root) {
		foreach (TaskDefinition task in TaskConfigurations.Load(root).Tasks) yield return ($"\"{task.Label}\"", string.Empty);
	}

	private static IEnumerable<(string, string)> SettingValues(SettingDefinition setting) {
		if (setting.Enum is { } options) {
			for (int i = 0; i < options.Length; i++) {
				string literal = setting.Types is ["string"] || setting.Types.Length == 0 ? JsonValue.Create(options[i]).ToJsonString() : options[i];
				yield return (literal, setting.EnumDescriptions is { } descriptions && i < descriptions.Length ? descriptions[i] : string.Empty);
			}

			yield break;
		}

		if (setting.IsBoolean) {
			yield return ("true", string.Empty);
			yield return ("false", string.Empty);
			yield break;
		}

		if (setting.Default is { } value) yield return (value.ToJsonString(), "Default value");
	}

	private static string SnippetFor(string[] types, JsonNode? fallback) {
		if (fallback is not null) {
			string json = fallback.ToJsonString();

			if (types is ["string"] && fallback is JsonValue) return json.Length > 2 ? json : "\"$0\"";

			return json.Length > 60 || json.Contains('\n') ? (types.Length > 0 && types[0] == "array" ? "[$0]" : types.Length > 0 && types[0] == "object" ? "{$0}" : json) : json;
		}

		return types.Length == 0 ? "$0" : types[0] switch {
			"string" => "\"$0\"",
			"boolean" => "false",
			"number" or "integer" => "0",
			"array" => "[$0]",
			"object" => "{$0}",
			_ => "$0",
		};
	}

	private static readonly Property[] LaunchProperties = [
		new("type", "Type of configuration.", "\"$0\"", ["\"coreclr\"", "\"node\"", "\"pwa-node\"", "\"chrome\"", "\"msedge\"", "\"python\""]),
		new("request", "Request type of configuration. Can be \"launch\" or \"attach\".", "\"launch\"", ["\"launch\"", "\"attach\""]),
		new("name", "Name of configuration; appears in the launch configuration dropdown menu.", "\"$0\""),
		new("program", "Path to the application dll or .NET Core host executable to launch.", "\"${workspaceFolder}/$0\""),
		new("args", "Command line arguments passed to the program.", "[$0]"),
		new("cwd", "Path to the working directory of the program being debugged. Default is the current workspace.", "\"${workspaceFolder}\""),
		new("env", "Environment variables passed to the program.", "{$0}"),
		new("envFile", "Environment variables passed to the program by a file.", "\"${workspaceFolder}/.env\""),
		new("console", "Where to launch the debug target.", "\"internalConsole\"", ["\"internalConsole\"", "\"integratedTerminal\"", "\"externalTerminal\""], ["Output to the Debug Console. This doesn't support reading console input.", "The integrated terminal.", "An external terminal that can be configured via user settings."]),
		new("stopAtEntry", "If true, the debugger should stop at the entry point of the target.", "false", ["true", "false"]),
		new("justMyCode", "When enabled (the default), the debugger only displays and steps into user-written code.", "true", ["true", "false"]),
		new("requireExactSource", "Flag to require current source code to match the pdb.", "true", ["true", "false"]),
		new("enableStepFiltering", "Flag to enable stepping over Properties and Operators.", "true", ["true", "false"]),
		new("processId", "The process id to attach to.", "\"${command:pickProcess}\""),
		new("preLaunchTask", "Task to run before debug session starts.", "\"$0\""),
		new("postDebugTask", "Task to run after debug session ends.", "\"$0\""),
		new("internalConsoleOptions", "Controls when the internal Debug Console should open.", "\"openOnSessionStart\"", ["\"neverOpen\"", "\"openOnFirstSessionStart\"", "\"openOnSessionStart\""]),
		new("sourceFileMap", "Maps build-time paths to local source locations.", "{$0}"),
		new("launchSettingsProfile", "If specified, indicates the name of the profile in launchSettings.json to use.", "\"$0\""),
		new("presentation", "Presentation options on how to show this configuration in the debug configuration dropdown and the command palette.", "{$0}"),
	];

	private static readonly Property[] TaskProperties = [
		new("label", "The task's user interface label", "\"$0\""),
		new("type", "Defines whether the task is run as a process or as a command inside a shell.", "\"shell\"", ["\"shell\"", "\"process\""]),
		new("command", "The command to be executed. Can be an external program or a shell command.", "\"$0\""),
		new("args", "Arguments passed to the command when this task is invoked.", "[$0]"),
		new("options", "Additional command options", "{$0}"),
		new("group", "Defines to which execution group this task belongs to. It supports \"build\" to add it to the build group and \"test\" to add it to the test group.", "\"build\"", ["\"build\"", "\"test\"", "\"none\""]),
		new("problemMatcher", "The problem matcher(s) to use. Can either be a string or a problem matcher definition or an array of strings and problem matchers.", "\"$msCompile\"", ["\"$msCompile\"", "\"$tsc\"", "\"$tsc-watch\"", "\"$eslint-stylish\"", "\"$gcc\""]),
		new("dependsOn", "Either a string representing another task or an array of other tasks that this task depends on.", "\"$0\""),
		new("dependsOrder", "Determines the order of the dependsOn tasks for this task.", "\"parallel\"", ["\"parallel\"", "\"sequence\""]),
		new("isBackground", "Whether the executed task is kept alive and is running in the background.", "false", ["true", "false"]),
		new("presentation", "Configures the panel that is used to present the task's output and reads its input.", "{$0}"),
		new("detail", "An optional description of a task that shows in the Run Task quick pick as a detail.", "\"$0\""),
		new("hide", "Hides this task from the run task quick pick.", "false", ["true", "false"]),
		new("runOptions", "The task's run related options.", "{$0}"),
	];

	private static readonly Property[] PresentationProperties = [
		new("reveal", "Controls whether the terminal running the task is revealed or not.", "\"always\"", ["\"always\"", "\"silent\"", "\"never\""]),
		new("panel", "Controls if the panel is shared between tasks, dedicated to this task or a new one is created on every run.", "\"shared\"", ["\"shared\"", "\"dedicated\"", "\"new\""]),
		new("focus", "Controls whether the panel takes focus.", "false", ["true", "false"]),
		new("echo", "Controls whether the executed command is echoed to the panel.", "true", ["true", "false"]),
		new("clear", "Controls whether the terminal is cleared before executing the task.", "false", ["true", "false"]),
		new("showReuseMessage", "Controls whether to show the `Terminal will be reused by tasks, press any key to close it` message.", "true", ["true", "false"]),
		new("close", "Controls whether the terminal the task runs in is closed when the task exits.", "false", ["true", "false"]),
	];

	public Task<CompletionDetail?> ResolveCompletionAsync(int index) => Task.FromResult<CompletionDetail?>(null);

	public Task<SignatureHelp?> SignatureHelpAsync(string path, int line, int column, char trigger) => Task.FromResult<SignatureHelp?>(null);

	public Task<string?> HoverAsync(string path, int line, int column) {
		if (KindOf(path) != FileKind.Settings || textOf(path) is not { } text) return Task.FromResult<string?>(null);

		int offset = OffsetOf(text, line - 1, column - 1);
		int start = text.LastIndexOf('"', Math.Max(0, offset - 1));
		int end = start < 0 ? -1 : text.IndexOf('"', start + 1);

		if (start < 0 || end < offset || end - start > 200) return Task.FromResult<string?>(null);

		string key = text[(start + 1)..end];
		return Task.FromResult(SettingsSchema.Find(key)?.Description is { Length: > 0 } description ? description : null);
	}
}
