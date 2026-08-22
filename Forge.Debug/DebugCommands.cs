using Forge.Common.Commands;
using Forge.Common.Debug;

namespace Forge.Debug;

public static class DebugCommands
{
	public const string Start = "workbench.action.debug.start";
	public const string Run = "workbench.action.debug.run";
	public const string Continue = "workbench.action.debug.continue";
	public const string Pause = "workbench.action.debug.pause";
	public const string StepOver = "workbench.action.debug.stepOver";
	public const string StepInto = "workbench.action.debug.stepInto";
	public const string StepOut = "workbench.action.debug.stepOut";
	public const string StepBack = "workbench.action.debug.stepBack";
	public const string ReverseContinue = "workbench.action.debug.reverseContinue";
	public const string Restart = "workbench.action.debug.restart";
	public const string Stop = "workbench.action.debug.stop";
	public const string Disconnect = "workbench.action.debug.disconnect";
	public const string DisconnectAndSuspend = "workbench.action.debug.disconnectAndSuspend";
	public const string SelectAndStart = "workbench.action.debug.selectandstart";
	public const string Configure = "workbench.action.debug.configure";
	public const string AddConfiguration = "debug.addConfiguration";
	public const string ToggleBreakpoint = "editor.debug.action.toggleBreakpoint";
	public const string EnableAllBreakpoints = "workbench.debug.viewlet.action.enableAllBreakpoints";
	public const string DisableAllBreakpoints = "workbench.debug.viewlet.action.disableAllBreakpoints";
	public const string RemoveAllBreakpoints = "workbench.debug.viewlet.action.removeAllBreakpoints";
	public const string ToggleBreakpointsActivated = "workbench.debug.viewlet.action.toggleBreakpointsActivatedAction";
	public const string RemoveAllWatch = "workbench.debug.viewlet.action.removeAllWatchExpressions";
	public const string FocusRepl = "workbench.debug.action.focusRepl";
	public const string ToggleRepl = "workbench.debug.action.toggleRepl";
	public const string CallStackTop = "workbench.action.debug.callStackTop";
	public const string CallStackBottom = "workbench.action.debug.callStackBottom";
	public const string CallStackUp = "workbench.action.debug.callStackUp";
	public const string CallStackDown = "workbench.action.debug.callStackDown";
	public const string RunTask = "workbench.action.tasks.runTask";
	public const string RunBuildTask = "workbench.action.tasks.build";
	public const string RunTestTask = "workbench.action.tasks.test";
	public const string ConfigureTasks = "workbench.action.tasks.configureTaskRunner";

	public const string DebugConsoleView = "Debug Console";

	private const string Category = "Debug";
	private const string TaskCategory = "Tasks";

	public static void Register(ICommandRegistry commands, DebugService debugger,
		Func<string, string, string?, Action<ICommandHost>, ICommand> factory) {
		void Add(string id, string title, Action<ICommandHost> handler, string category = Category) =>
			commands.Register(factory(id, title, category, host => {
				handler(host);
				host.Refresh();
			}));

		Add(Start, "Start Debugging", _ => debugger.StartDebugging(null, noDebug: false));
		Add(Run, "Start Without Debugging", _ => debugger.StartDebugging(null, noDebug: true));
		Add(Continue, "Continue", _ => debugger.Continue());
		Add(Pause, "Pause", _ => debugger.Pause());
		Add(StepOver, "Step Over", _ => debugger.StepOver());
		Add(StepInto, "Step Into", _ => debugger.StepInto());
		Add(StepOut, "Step Out", _ => debugger.StepOut());
		Add(StepBack, "Step Back", _ => debugger.StepBack());
		Add(ReverseContinue, "Reverse", _ => debugger.ReverseContinue());
		Add(Restart, "Restart", _ => debugger.RestartSession());
		Add(Stop, "Stop", _ => debugger.StopSession(disconnect: false, suspend: false));
		Add(Disconnect, "Disconnect", _ => debugger.StopSession(disconnect: true, suspend: false));
		Add(DisconnectAndSuspend, "Disconnect and Suspend",
			_ => debugger.StopSession(disconnect: true, suspend: true));

		Add(SelectAndStart, "Select and Start Debugging", host => host.ShowCommandPalette());

		foreach (DebugConfiguration configuration in debugger.GetConfigurations()) {
			string name = configuration.Name;

			Add($"debug.start.{name}", $"Start Debugging: {name}", _ => {
				debugger.SelectConfiguration(name);
				debugger.StartDebugging(name, noDebug: false);
			});
		}

		Add(Configure, "Open launch.json",
			host => OpenOrCreate(host, LaunchConfigurations.FileFor(Root(host, debugger)), DefaultLaunchJson));
		Add(AddConfiguration, "Add Configuration...",
			host => OpenOrCreate(host, LaunchConfigurations.FileFor(Root(host, debugger)), DefaultLaunchJson));

		Add(ToggleBreakpoint, "Toggle Breakpoint", host => {
			if (host.GetActiveFilePath() is { Length: > 0 } path) {
				debugger.ToggleBreakpoint(path, host.GetCaretLine());
			}
		});

		Add(EnableAllBreakpoints, "Enable All Breakpoints",
			_ => debugger.EnableOrDisableBreakpoints(true, null, 0));
		Add(DisableAllBreakpoints, "Disable All Breakpoints",
			_ => debugger.EnableOrDisableBreakpoints(false, null, 0));
		Add(RemoveAllBreakpoints, "Remove All Breakpoints", _ => debugger.RemoveAllBreakpoints());
		Add(ToggleBreakpointsActivated, "Toggle Activate Breakpoints",
			_ => debugger.SetBreakpointsActivated(!debugger.AreBreakpointsActivated()));

		Add(RemoveAllWatch, "Remove All Expressions", _ => debugger.RemoveAllWatchExpressions());

		Add(FocusRepl, "Focus on Debug Console View", host => host.ShowPanel(DebugConsoleView));
		Add(ToggleRepl, "Toggle Debug Console", host => host.ShowPanel(DebugConsoleView));

		Add(CallStackTop, "Focus Top Stack Frame", _ => FocusFrame(debugger, 0));
		Add(CallStackBottom, "Focus Bottom Stack Frame", _ => FocusFrame(debugger, int.MaxValue));
		Add(CallStackUp, "Focus Previous Stack Frame", _ => MoveFrame(debugger, -1));
		Add(CallStackDown, "Focus Next Stack Frame", _ => MoveFrame(debugger, 1));

		Add(RunTask, "Run Task...", host => host.ShowCommandPalette(), TaskCategory);

		TaskConfigurations tasks = TaskConfigurations.Load(debugger.WorkspaceFolder);

		foreach (TaskDefinition task in tasks.Tasks) {
			TaskDefinition definition = task;

			Add($"{RunTask}.{definition.Label}", $"Run Task: {definition.Label}",
				host => RunChain(host, tasks, definition, Root(host, debugger)), TaskCategory);
		}

		Add(RunBuildTask, "Run Build Task", host => RunGroup(host, debugger, "build"), TaskCategory);
		Add(RunTestTask, "Run Test Task", host => RunGroup(host, debugger, "test"), TaskCategory);
		Add(ConfigureTasks, "Configure Tasks...",
			host => OpenOrCreate(host, TaskConfigurations.FileFor(Root(host, debugger)), DefaultTasksJson),
			TaskCategory);
	}

	public static void RunChain(ICommandHost host, TaskConfigurations tasks, TaskDefinition task,
		string rootPath) {
		ResolverContext context = new(rootPath) {
			File = host.GetActiveFilePath(),
			LineNumber = host.GetCaretLine(),
			ColumnNumber = host.GetCaretColumn(),
		};

		foreach (TaskDefinition entry in tasks.Chain(task)) {
			TaskDefinition step = TaskConfigurations.Resolve(entry, context);

			if (step.CommandLine is not { Length: > 0 } commandLine) continue;

			host.RunInTerminal(step.Label, commandLine, step.Cwd ?? rootPath, null, null);
		}
	}

	private static void RunGroup(ICommandHost host, DebugService debugger, string group) {
		string root = Root(host, debugger);
		TaskConfigurations tasks = TaskConfigurations.Load(root);

		if (tasks.DefaultFor(group) is not { } task) {
			host.Log($"No default {group} task. Add one to .vscode/tasks.json.");
			host.ShowPanel("Output");
			return;
		}

		RunChain(host, tasks, task, root);
	}

	private static string Root(ICommandHost host, DebugService debugger) =>
		host.GetWorkspacePath() ?? debugger.WorkspaceFolder;

	private static void FocusFrame(DebugService debugger, int index) {
		IReadOnlyList<DebugStackFrame> frames = debugger.GetCallStack();

		if (frames.Count == 0) return;

		debugger.FocusStackFrame(frames[Math.Clamp(index, 0, frames.Count - 1)].Id);
	}

	private static void MoveFrame(DebugService debugger, int delta) {
		IReadOnlyList<DebugStackFrame> frames = debugger.GetCallStack();

		if (frames.Count == 0) return;

		int current = 0;

		if (debugger.GetFocusedFrame() is { } focused) {
			for (int i = 0; i < frames.Count; i++) {
				if (frames[i].Id == focused.Id) {
					current = i;
					break;
				}
			}
		}

		debugger.FocusStackFrame(frames[Math.Clamp(current + delta, 0, frames.Count - 1)].Id);
	}

	private static void OpenOrCreate(ICommandHost host, string path, string template) {
		try {
			if (!File.Exists(path)) {
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, template);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			host.Log($"Could not create {path}: {exception.Message}");
			return;
		}

		host.OpenFile(path);
	}

	private const string DefaultLaunchJson = """
		{
			"version": "0.2.0",
			"configurations": [
				{
					"name": ".NET Core Launch",
					"type": "coreclr",
					"request": "launch",
					"preLaunchTask": "build",
					"program": "${workspaceFolder}/bin/Debug/net10.0/${workspaceFolderBasename}.dll",
					"cwd": "${workspaceFolder}",
					"console": "internalConsole",
					"stopAtEntry": false
				}
			]
		}
		""";

	private const string DefaultTasksJson = """
		{
			"version": "2.0.0",
			"tasks": [
				{
					"label": "build",
					"type": "shell",
					"command": "dotnet",
					"args": ["build"],
					"group": { "kind": "build", "isDefault": true }
				}
			]
		}
		""";
}
