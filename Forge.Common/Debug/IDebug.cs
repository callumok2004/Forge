namespace Forge.Common.Debug;

public interface IDebug
{
	DebugState GetState();

	bool IsActive();

	IReadOnlyList<DebugConfiguration> GetConfigurations();

	string? GetSelectedConfiguration();

	void SelectConfiguration(string name);

	void ReloadConfigurations();

	bool StartDebugging(string? configurationName, bool noDebug);

	void RestartSession();

	void StopSession(bool disconnect, bool suspend);

	void Continue();

	void Pause();

	void StepOver();

	void StepInto();

	void StepOut();

	void StepBack();

	void ReverseContinue();

	IReadOnlyList<SourceBreakpoint> GetBreakpoints();

	IReadOnlyList<SourceBreakpoint> GetBreakpointsFor(string path);

	bool ToggleBreakpoint(string path, int line);

	void AddBreakpoint(SourceBreakpoint breakpoint);

	void UpdateBreakpoint(string path, int line, string? condition, string? hitCondition, string? logMessage);

	void RemoveBreakpoint(string path, int line);

	void RemoveAllBreakpoints();

	void EnableOrDisableBreakpoints(bool enable, string? path, int line);

	void SetBreakpointsActivated(bool activated);

	bool AreBreakpointsActivated();

	IReadOnlyList<FunctionBreakpoint> GetFunctionBreakpoints();

	void AddFunctionBreakpoint(string name);

	void RemoveFunctionBreakpoint(string name);

	IReadOnlyList<ExceptionBreakpoint> GetExceptionBreakpoints();

	void SetExceptionBreakpoint(string filter, bool enabled, string? condition);

	IReadOnlyList<DebugThread> GetThreads();

	IReadOnlyList<DebugStackFrame> GetCallStack();

	DebugStackFrame? GetFocusedFrame();

	void FocusStackFrame(int frameId);

	DebugCapabilities GetCapabilities();

	Task<IReadOnlyList<DebugScope>> ScopesAsync(int frameId);

	Task<IReadOnlyList<DebugVariable>> VariablesAsync(int variablesReference);

	Task<DebugVariable?> EvaluateAsync(string expression, string context);

	IReadOnlyList<string> GetWatchExpressions();

	void AddWatchExpression(string expression);

	void RenameWatchExpression(int index, string expression);

	void RemoveWatchExpression(int index);

	void RemoveAllWatchExpressions();

	void OnStateChanged(Action handler);

	void OnModelChanged(Action handler);

	void OnOutput(Action<DebugOutput> handler);
}
