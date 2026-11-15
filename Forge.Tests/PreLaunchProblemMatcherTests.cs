using System.Reflection;

using Forge.Common.Commands;
using Forge.Debug;

namespace Forge.Tests;

public sealed class PreLaunchProblemMatcherTests : IDisposable
{
	private const string ForgeTasks = """
		{
		  "version": "2.0.0",
		  "tasks": [
		    {
		      "label": "build App",
		      "command": "dotnet",
		      "type": "process",
		      "args": [
		        "build",
		        // "--no-restore",
		        "--no-incremental",
		        "${workspaceFolder}/App/App.csproj",
		        // "-p:WarningLevel=0"
		      ],
		      "problemMatcher": "$msCompile"
		    },
		    {
		      "label": "no matcher",
		      "command": "echo",
		      "type": "process",
		      "args": ["hi"]
		    },
		    {
		      "label": "array matcher",
		      "command": "dotnet",
		      "type": "process",
		      "args": ["build"],
		      "problemMatcher": ["$msCompile", { "base": "$tsc" }]
		    }
		  ]
		}
		""";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "forge-prelaunch-tests", Guid.NewGuid().ToString("N"));

	public PreLaunchProblemMatcherTests() {
		Directory.CreateDirectory(Path.Combine(_root, ".forge"));
		Directory.CreateDirectory(Path.Combine(_root, ".vscode"));
		File.WriteAllText(Path.Combine(_root, ".forge", "tasks.json"), ForgeTasks);
		File.WriteAllText(Path.Combine(_root, ".vscode", "tasks.json"), """{ "version": "2.0.0", "tasks": [ { "label": "build App", "command": "dotnet", "type": "process", "args": ["build"] } ] }""");
	}

	public void Dispose() {
		try {
			Directory.Delete(_root, true);
		}
		catch (IOException) {
		}
	}

	[Fact]
	public void ForgeTasksFileTakesPriorityAndKeepsMatcher() {
		TaskDefinition task = TaskConfigurations.Load(_root).Find("build App")!;

		Assert.Equal(["$msCompile"], task.ProblemMatchers);
		Assert.Contains("--no-incremental", task.Args);
	}

	[Fact]
	public void TaskWithoutMatcherHasNone() => Assert.Empty(TaskConfigurations.Load(_root).Find("no matcher")!.ProblemMatchers);

	[Fact]
	public void MatcherArrayAndBaseObjectAreRead() => Assert.Equal(["$msCompile", "$tsc"], TaskConfigurations.Load(_root).Find("array matcher")!.ProblemMatchers);

	[Fact]
	public void PreLaunchTaskPassesMatchersToRunner() {
		DebugService debugger = new();
		typeof(DebugService).GetProperty(nameof(DebugService.WorkspaceFolder))!.SetValue(debugger, _root);
		List<(string Label, IReadOnlyList<string> Matchers)> runs = [];
		debugger.TaskRunner = (label, _, _, matchers, finished) => {
			runs.Add((label, matchers));
			return true;
		};

		MethodInfo run = typeof(DebugService).GetMethod("RunTaskByLabel", BindingFlags.NonPublic | BindingFlags.Instance)!;
		Assert.True((bool)run.Invoke(debugger, ["build App", null, null])!);

		(string label, IReadOnlyList<string> matchers) = Assert.Single(runs);
		Assert.Equal("build App", label);
		Assert.Equal(["$msCompile"], matchers);
	}

	private sealed class Recording(List<string> lines, List<int> exits) : IProblemMatcherRun
	{
		public void Read(string line) => lines.Add(line);

		public void Finished(int exitCode) => exits.Add(exitCode);
	}

	[Fact]
	public void RegisteredMatcherReceivesLinesAndExit() {
		List<string> lines = [];
		List<int> exits = [];
		string name = "$test-" + Guid.NewGuid().ToString("N");
		ProblemMatchers.Register(name, _ => new Recording(lines, exits));

		(Action<string>? onLine, Action<int>? onExit) = ProblemMatchers.Start(null!, [name]);
		onLine!("a(1,1): warning CS0168: x");
		onExit!(0);

		Assert.Equal(["a(1,1): warning CS0168: x"], lines);
		Assert.Equal([0], exits);
	}

	[Fact]
	public void UnknownMatcherGivesNoObservers() {
		(Action<string>? onLine, Action<int>? onExit) = ProblemMatchers.Start(null!, ["$nope-" + Guid.NewGuid().ToString("N")]);

		Assert.Null(onLine);
		Assert.Null(onExit);
	}
}
