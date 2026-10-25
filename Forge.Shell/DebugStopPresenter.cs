using Forge.Common.Debug;
using Forge.Core;
using Forge.Core.Native;
using Forge.Editor;
using Forge.Workbench;

namespace Forge.Shell;

public sealed class DebugStopPresenter(Workbench workbench, ShellContext context, nint window)
{
	public ExceptionWidget Widget { get; } = new();

	private TextView? _zoneView;

	public void Reveal(DebugReveal reveal) {
		workbench.OpenFile(reveal.Path);

		if (workbench.ActiveView is not { } view || !string.Equals(workbench.ActiveFilePath, reveal.Path, StringComparison.OrdinalIgnoreCase)) return;

		int line = Math.Clamp(reveal.Line - 1, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		Forge.Text.TextSpan span = view.Snapshot.GetLineSpan(line);

		view.PushUndoStop();
		view.MoveTo(Math.Clamp(span.Start + Math.Max(0, reveal.Column - 1), span.Start, span.End), extend: false);
		view.RevealLineInCenterIfOutsideViewport(line, context.Painter.GetCode().GetLineHeight(), workbench.EditorHeight(context.Device.Height));

		if (Settings.Current.GetBoolean("debug.focusEditorOnBreak") ?? true) workbench.FocusEditor?.Invoke();

		if (reveal.IsTopFrame && (Settings.Current.GetBoolean("debug.focusWindowOnBreak") ?? true)) Win32.SetForegroundWindow(window);

		if (reveal.Exception is { } exception) ShowException(view, exception, reveal.Path, reveal.Line);

		context.RefreshEditor();
	}

	private void ShowException(TextView view, DebugExceptionInfo exception, string path, int line) {
		ClearZone();
		Widget.Closed = ClearZone;
		Widget.Show(exception, path, line);
		_zoneView = view;
		Widget.ZoneId = view.AddZone(line - 1, 0f);
	}

	public void Clear() => Widget.Hide();

	private void ClearZone() {
		if (_zoneView is { } view && Widget.ZoneId >= 0) view.RemoveZone(Widget.ZoneId);

		_zoneView = null;
		Widget.ZoneId = -1;
		context.RefreshEditor();
	}

	public void Place(float textLeft, float editorTop, float textWidth, float lineHeight, float originX, float originY) {
		if (!Widget.IsOpen || _zoneView is not { } view || workbench.ActiveView != view) {
			Widget.Visible = false;
			return;
		}

		float height = Widget.Measure(textWidth);

		if (view.Zones.FirstOrDefault(zone => zone.Id == Widget.ZoneId) is { } zone && zone.Height != height) {
			view.ResizeZone(Widget.ZoneId, height);
			context.RefreshEditor();
		}

		Widget.Visible = true;
		Widget.SetBounds(textLeft - originX, editorTop + view.ZoneTop(Widget.ZoneId, lineHeight) - view.ScrollY - originY, textWidth, height);
	}
}
