namespace Forge.Common.Debug;

public enum DebugState
{
	Inactive,
	Initializing,
	Stopped,
	Running,
}

public enum BreakpointKind
{
	Source,
	Function,
	Data,
	Instruction,
	Exception,
}

public sealed record SourceBreakpoint(
	string Path,
	int Line,
	string? Condition = null,
	string? HitCondition = null,
	string? LogMessage = null,
	bool Enabled = true)
{
	public bool Verified { get; init; }

	public int? VerifiedLine { get; init; }

	public string? Message { get; init; }

	public int EffectiveLine => Verified && VerifiedLine is { } line ? line : Line;
}

public sealed record FunctionBreakpoint(string Name, string? Condition = null,
	string? HitCondition = null, bool Enabled = true);

public sealed record DataBreakpoint(string DataId, string Description, string? AccessType = null,
	string? Condition = null, string? HitCondition = null, bool Enabled = true);

public sealed record InstructionBreakpoint(string InstructionReference, long Offset = 0,
	string? Condition = null, string? HitCondition = null, bool Enabled = true);

public sealed record ExceptionBreakpoint(string Filter, string Label, bool Enabled,
	string? Condition = null);

public sealed record DebugThread(int Id, string Name, bool Stopped, string? StopReason);

public sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line, int Column,
	string? PresentationHint, string? InstructionReference);

public sealed record DebugScope(string Name, int VariablesReference, bool Expensive,
	string? PresentationHint);

public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference,
	string? EvaluateName, bool ReadOnly, int NamedCount, int IndexedCount);

public sealed record DebugOutput(string Category, string Text, string? SourcePath, int Line);

public sealed record DebugCapabilities
{
	public bool ConfigurationDone { get; init; }
	public bool ConditionalBreakpoints { get; init; }
	public bool HitConditionalBreakpoints { get; init; }
	public bool LogPoints { get; init; }
	public bool FunctionBreakpoints { get; init; }
	public bool DataBreakpoints { get; init; }
	public bool InstructionBreakpoints { get; init; }
	public bool StepBack { get; init; }
	public bool StepInTargets { get; init; }
	public bool RestartFrame { get; init; }
	public bool RestartRequest { get; init; }
	public bool TerminateRequest { get; init; }
	public bool TerminateThreads { get; init; }
	public bool SuspendDebuggee { get; init; }
	public bool SetVariable { get; init; }
	public bool SetExpression { get; init; }
	public bool EvaluateForHovers { get; init; }
	public bool ExceptionInfo { get; init; }
	public bool Disassemble { get; init; }
	public bool ReadMemory { get; init; }
	public bool LoadedSources { get; init; }
	public bool Modules { get; init; }
	public bool GotoTargets { get; init; }
	public bool CompletionsRequest { get; init; }
	public bool ClipboardContext { get; init; }
	public IReadOnlyList<ExceptionBreakpoint> ExceptionFilters { get; init; } = [];
}

public sealed record DebugConfiguration(string Name, string Type, string Request)
{
	public string? Program { get; init; }
	public string? Cwd { get; init; }
	public string[]? Args { get; init; }
	public string? Console { get; init; }
	public string? PreLaunchTask { get; init; }
	public string? PostDebugTask { get; init; }
	public bool StopAtEntry { get; init; }
	public IReadOnlyDictionary<string, string> Environment { get; init; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	public System.Text.Json.Nodes.JsonObject? Raw { get; init; }
}

public sealed record DebugCompound(string Name, IReadOnlyList<string> Configurations,
	string? PreLaunchTask);

public enum BreakpointMark
{
	None,
	Verified,
	Unverified,
	Disabled,
	Conditional,
	LogPoint,
}
