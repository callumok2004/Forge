using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Extensions.DotnetProfiler.Engine;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Extensions.DotnetProfiler.Views;

public sealed class ProcessListView : Panel
{
	private readonly DotnetProfilerService _service;
	private readonly ListPanel _list;
	private bool _polling;

	public ProcessListView(DotnetProfilerService service) {
		_service = service;
		Name = "dotnetProfiler.processes";
		MouseInputEnabled = false;

		_list = new ListPanel { Dock = DockType.Fill, RowPainter = PaintRow, Activated = Attach };
		AddChild(_list);

		service.Changed += change => {
			if ((change & DotnetProfilerChange.Processes) == 0) return;

			_list.SetCount(service.Processes.Count);
			_list.Repaint();
		};

		_list.SetCount(service.Processes.Count);
		AddTickSignal(ShowCheckMilliseconds);
	}

	private void Attach(int index) {
		IReadOnlyList<ProcessInfo> processes = _service.Processes;

		if ((uint)index < (uint)processes.Count) _ = _service.AttachAsync(processes[index].Pid, processes[index].Name, null);
	}

	private const int ShowCheckMilliseconds = 500;

	private long _lastRefresh;

	public override void OnTick() {
		bool showing = IsShowing;
		int interval = _service.Options.ProcessListRefreshMs;

		if (!showing) {
			_polling = false;
			return;
		}

		long now = Environment.TickCount64;

		if (!_polling || (interval > 0 && now - _lastRefresh >= interval)) {
			_polling = true;
			_lastRefresh = now;
			_service.RefreshProcesses(force: true);
		}
	}

	private void PaintRow(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		IReadOnlyList<ProcessInfo> processes = _service.Processes;

		if ((uint)index >= (uint)processes.Count) return;

		ProcessInfo process = processes[index];
		bool attached = _service.Active?.Pid == process.Pid;
		float scale = Scale;
		ITextFont font = painter.GetUi();
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float iconX = left + (16f * scale);

		painter.DrawIcon(attached ? Codicon.Record : Codicon.SymbolMethod, iconX, top + (height / 2f), 14f * scale, attached ? Theme.ChartsRed : Theme.IconForeground);

		float textX = left + (28f * scale);
		painter.DrawText(font, process.Name, textX, baseline, Theme.SidebarFileForeground);

		string description = attached ? $"pid {process.Pid} · profiling" : $"pid {process.Pid}";
		painter.DrawText(painter.GetUiDescription(), description, textX + font.Measure(process.Name) + (6f * scale), baseline, Theme.DescriptionForeground);
	}
}

public sealed class SessionView : Panel
{
	private const float RowHeight = 22f;
	private const float ButtonSize = 20f;

	private readonly DotnetProfilerService _service;
	private readonly ActionButton _record;
	private readonly ActionButton _detach;
	private bool _hover;

	public SessionView(DotnetProfilerService service) {
		_service = service;
		Name = "dotnetProfiler.sessions";

		_record = new ActionButton { Icon = Codicon.Record, Name = "Start CPU Profiling", Action = service.ToggleRecording, Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground };
		_detach = new ActionButton { Icon = Codicon.DebugDisconnect, Name = "Detach", Action = () => service.Detach(), Foreground = Theme.ChromeForeground, HoverBackground = Theme.ListHoverBackground };
		AddChild(_record);
		AddChild(_detach);

		service.Changed += change => {
			if ((change & (DotnetProfilerChange.Session | DotnetProfilerChange.Recording)) == 0) return;

			Sync();
		};

		Sync();
	}

	private void Sync() {
		bool live = _service.Live;
		_record.Visible = _detach.Visible = live;
		_record.Icon = _service.Recording ? Codicon.DebugStop : Codicon.Record;
		InvalidateLayout();
		Repaint();
	}

	public override void OnCursorEntered() {
		_hover = true;
		Repaint();
	}

	public override void OnCursorExited() {
		_hover = false;
		Repaint();
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button != MouseButton.Left || !_service.Live || y > RowHeight * Scale) return false;

		_service.OpenEditor();
		return true;
	}

	public override void PerformLayout() {
		float scale = Scale;
		float size = ButtonSize * scale;
		float top = ((RowHeight * scale) - size) / 2f;

		_detach.SetBounds(Wide - size - (8f * scale), top, size, size);
		_record.SetBounds(Wide - (size * 2f) - (10f * scale), top, size, size);
	}

	public override void Paint(IPainter painter) {
		float scale = Scale;
		float height = RowHeight * scale;
		ITextFont font = painter.GetUi();
		float baseline = ScreenY + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();

		if (_service.Active is not { } session) {
			painter.DrawText(font, "No active session. Attach to a process to start.", ScreenX + (12f * scale), baseline, Theme.DescriptionForeground);
			return;
		}

		if (_hover) painter.FillRectangle(ScreenX, ScreenY, Wide, height, Theme.ListHoverBackground);

		bool recording = session.Recording;
		painter.DrawIcon(recording ? Codicon.Record : Codicon.Pulse, ScreenX + (16f * scale), ScreenY + (height / 2f), 14f * scale, recording ? Theme.ChartsRed : Theme.IconForeground);

		float textX = ScreenX + (28f * scale);
		painter.DrawText(font, session.Name, textX, baseline, Theme.SidebarFileForeground);
		painter.DrawText(painter.GetUiDescription(), recording ? $"pid {session.Pid} · recording" : $"pid {session.Pid}", textX + font.Measure(session.Name) + (6f * scale), baseline, Theme.DescriptionForeground);
	}
}
