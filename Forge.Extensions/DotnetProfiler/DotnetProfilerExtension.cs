using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Views;

namespace Forge.Extensions.DotnetProfiler;

public sealed class DotnetProfilerExtension : IForgeExtension
{
	public const string SidebarContainer = "dotnetProfiler";
	public const string PanelContainer = "dotnetProfilerGraphs";
	private const string Category = ".NET Profiler";

	private readonly DotnetProfilerService _service = new();
	private SourceAnnotations? _annotations;
	private LiveMetricsView? _live;

	public DotnetProfilerService Service => _service;

	public string GetIdentifier() => "forge.dotnet-profiler";

	public string GetDisplayName() => ".NET Profiler";

	public ActivationEvent GetActivation() => ActivationEvent.Startup;

	public bool ActivatesIn(string? workspacePath) => true;

	public bool ActivatesFor(string filePath) => false;

	public bool ActivatesForCommand(string commandId) => commandId.StartsWith("dotnetProfiler.", StringComparison.Ordinal);

	public void Register(ICommandRegistry commands) {
		Add("dotnetProfiler.open", "Open Profiler", _ => _service.OpenEditor());
		Add("dotnetProfiler.attach", "Attach to Process...", _ => _service.PickAndAttach());
		Add("dotnetProfiler.startCpu", "Start CPU Profiling", _ => _service.StartCpu(), () => _service.Live && !_service.Recording);
		Add("dotnetProfiler.stopCpu", "Stop CPU Profiling", host => _ = _service.StopCpuAsync(), () => _service.Recording);
		Add("dotnetProfiler.detach", "Detach", _ => _service.Detach(), () => _service.Live);
		Add("dotnetProfiler.snapshot", "Take Heap Snapshot", host => _ = _service.SnapshotAsync(), () => _service.Live);
		Add("dotnetProfiler.compareSnapshots", "Compare Last Two Snapshots", _ => _service.CompareSnapshots(), () => _service.Snapshots.Count >= 2);
		Add("dotnetProfiler.showGraphs", "Show Live Graphs Panel", _ => _service.ShowGraphs());
		Add("dotnetProfiler.cleanTempFiles", "Delete Temp Dumps and Traces", _ => _service.CleanTempFiles());
		Add("dotnetProfiler.setInterval", "Set Graph Refresh Interval...", _ => _service.PickInterval());
		Add("dotnetProfiler.enableProfileOnStart", "Start Profiling on Debug Start", _ => _service.SetProfileOnStart(true), () => !_service.ProfileOnStart);
		Add("dotnetProfiler.disableProfileOnStart", "Don't Start Profiling on Debug Start", _ => _service.SetProfileOnStart(false), () => _service.ProfileOnStart);
		Add("dotnetProfiler.setNamespaceFilter", "Limit Results to Namespace...", _ => {
			_service.ShowLive();
			_live?.FocusFilter();
		});
		Add("dotnetProfiler.clearNamespaceFilter", "Clear Namespace Limit", _ => _service.SetNamespaceFilter(string.Empty));
		Add("dotnetProfiler.refreshProcesses", "Refresh", _ => _service.RefreshProcesses(force: true));
		Add("dotnetProfiler.clearAnnotations", "Clear Inline Profile Annotations", _ => _annotations?.Clear());

		void Add(string id, string title, Action<ICommandHost> handler, Func<bool>? when = null) => commands.Register(new Command(id, title, Category, handler, When: when));
	}

	public void RegisterViews(IWorkbenchViews views) {
		views.RegisterContainer(new ViewContainer(SidebarContainer, ".NET Profiler", Codicon.Pulse, ViewLocation.Sidebar));
		views.RegisterContainer(new ViewContainer(PanelContainer, "Profiler Graphs", Codicon.Pulse, ViewLocation.Panel));

		views.RegisterView(new ViewDescriptor("dotnetProfiler.live", "Live Metrics", SidebarContainer, () => _live = new LiveMetricsView(_service, panel: false), 0) {
			Actions = [
				new("dotnetProfiler.open", Codicon.Pulse, "Open Profiler"),
				new("dotnetProfiler.enableProfileOnStart", Codicon.DebugStart, "Start Profiling on Debug Start") { Visible = () => !_service.ProfileOnStart },
				new("dotnetProfiler.disableProfileOnStart", Codicon.DebugRestart, "Don't Start Profiling on Debug Start") { Visible = () => _service.ProfileOnStart },
				new("dotnetProfiler.setNamespaceFilter", Codicon.Filter, "Limit Results to Namespace..."),
				new("dotnetProfiler.cleanTempFiles", Codicon.Trash, "Delete Temp Dumps and Traces"),
			],
		});
		views.RegisterView(new ViewDescriptor("dotnetProfiler.processes", ".NET Processes", SidebarContainer, () => new ProcessListView(_service), 1) {
			Actions = [new("dotnetProfiler.refreshProcesses", Codicon.Refresh, "Refresh")],
		});
		views.RegisterView(new ViewDescriptor("dotnetProfiler.sessions", "Active Session", SidebarContainer, () => new SessionView(_service), 2) {
			Actions = [new("dotnetProfiler.open", Codicon.Pulse, "Open Profiler")],
		});
		views.RegisterView(new ViewDescriptor("dotnetProfiler.graphs", "Live Graphs", PanelContainer, () => new LiveMetricsView(_service, panel: true)));
	}

	public void Activate(IExtensionContext context) {
		ICommandHost host = context.GetHost();
		_annotations = new SourceAnnotations(host);
		_service.CreateEditor = () => new DotnetProfilerEditor(_service, host, name => _ = _annotations.OpenAsync(name));
		_service.Attach(context.GetViews(), host, context.GetDebug());
		_service.Reconfigure(context.GetSettings());
		_service.View.Changed += Annotate;

		_ = Task.Run(() => Engine.TempFiles.Cleanup(TimeSpan.FromHours(1)));
	}

	private void Annotate() {
		if (_annotations is null) return;

		if (!_service.Options.InlineAnnotations || _service.View.Cpu is null) {
			_annotations.Clear();
			return;
		}

		_ = _annotations.SetAsync(_service.View);
	}

	public void Deactivate() {
		_service.View.Changed -= Annotate;
		_annotations?.Clear();
		_service.Dispose();
	}
}
