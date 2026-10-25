using System.Text.Json.Nodes;

using Forge.Core;

namespace Forge.Extensions.Dotnet;

public sealed class DotnetSettings
{
	public string? ServerPath { get; private set; }

	public string ServerLogLevel { get; private set; } = "Information";

	public bool UseServerGC { get; private set; }

	public string SourceGeneratorExecution { get; private set; } = "automatic";

	public IReadOnlyDictionary<string, string> ServerEnvironment { get; private set; } = new Dictionary<string, string>();

	public string? DotnetPath { get; private set; }

	public string? SolutionPath { get; private set; }

	public int MaxAutoLoad { get; private set; } = 100;

	public bool PreferImplementation { get; private set; } = true;

	public bool PreserveCommentIndentation { get; private set; } = true;

	public int TabSize { get; private set; } = 4;

	public bool InsertSpaces { get; private set; } = true;

	public string StorageDirectory { get; init; } = UserData.Directory;

	public void Read(Settings settings) {
		ServerPath = settings.GetString("dotnet.server.path") is { Length: > 0 } path ? path : null;
		ServerLogLevel = settings.GetString("dotnet.server.logLevel") ?? ServerLogLevel;
		UseServerGC = settings.GetBoolean("dotnet.server.useServerGC") ?? UseServerGC;
		SourceGeneratorExecution = settings.GetString("dotnet.server.sourceGeneratorExecution") ?? SourceGeneratorExecution;
		ServerEnvironment = ReadEnvironment(settings.GetRaw("dotnet.server.environmentVariables"));
		DotnetPath = settings.GetString("dotnet.dotnetPath") is { Length: > 0 } dotnet ? dotnet : null;
		SolutionPath = settings.GetString("dotnet.solution.path") is { Length: > 0 } solution ? solution : settings.GetString("dotnet.defaultSolution") is { Length: > 0 } fallback && fallback != "disable" ? fallback : null;
		MaxAutoLoad = settings.GetInt32("dotnet.projects.maxAutoLoad") ?? MaxAutoLoad;
		PreferImplementation = settings.GetBoolean("dotnet.navigation.preferImplementation") ?? PreferImplementation;
		PreserveCommentIndentation = settings.GetBoolean("dotnet.formatting.preserveCommentIndentation") ?? PreserveCommentIndentation;
		TabSize = settings.GetInt32("editor.tabSize") ?? TabSize;
		InsertSpaces = settings.GetBoolean("editor.insertSpaces") ?? InsertSpaces;
	}

	private static Dictionary<string, string> ReadEnvironment(JsonNode? node) {
		Dictionary<string, string> variables = [];

		if (node is not JsonObject values) return variables;

		foreach ((string name, JsonNode? value) in values) {
			if (value is JsonValue text && text.TryGetValue(out string? content)) variables[name] = content;
		}

		return variables;
	}
}
