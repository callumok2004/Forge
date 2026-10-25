using Forge.Common.Commands;
using Forge.Editor;
using Forge.Text;
using Forge.Workbench;
using Forge.Workspace;

namespace Forge.Shell;

public sealed class PeekPresenter(Workbench workbench, ShellContext context)
{
	public PeekReferencesWidget Widget { get; } = new();

	private TextView? _zoneView;
	private IReadOnlyList<HostLocation> _locations = [];
	private string _title = "References";

	public void Show(string title, int line, int column, IReadOnlyList<HostLocation> locations) {
		if (workbench.ActiveView is not { } view || workbench.ActiveFilePath is not { } path) return;

		ClearZone();

		_title = title;
		_locations = locations;
		_zoneView = view;

		int lineIndex = Math.Clamp(line - 1, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		TextSpan span = view.Snapshot.GetLineSpan(lineIndex);

		view.PushUndoStop();
		view.MoveTo(Math.Clamp(span.Start + Math.Max(0, column - 1), span.Start, span.End), extend: false);

		Widget.Closed = OnClosed;
		Widget.Open = OnOpen;
		Widget.LayoutChanged = OnLayoutChanged;
		Widget.Ratio = workbench.PeekRatio;
		Widget.HeightInLines = workbench.PeekHeightInLines;
		Widget.Show(title, line, column, locations, Load);
		Widget.ZoneId = view.AddZone(lineIndex, 0f);
		Widget.SelectNearest(path, line, column);
		Widget.RequestFocus();

		context.RefreshEditor();
	}

	private TextBuffer? Load(string path) {
		int open = workbench.IndexOf(path);

		if (open >= 0) return workbench.Documents[open].Buffer;

		try {
			return Document.Open(path).Buffer;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	private void OnOpen(HostLocation location, bool keepPeeking) {
		IReadOnlyList<HostLocation> locations = _locations;
		string title = _title;

		Widget.Close();
		workbench.GoTo(location.Path, location.Line, location.Column);

		if (keepPeeking) Show(title, location.Line, location.Column, locations);
		else workbench.RestoreEditorFocus();
	}

	private void OnLayoutChanged() {
		workbench.PeekRatio = Widget.Ratio;
		workbench.PeekHeightInLines = Widget.HeightInLines;
		context.RefreshEditor();
	}

	private void OnClosed() {
		ClearZone();
		workbench.RestoreEditorFocus();
	}

	private void ClearZone() {
		if (_zoneView is { } view && Widget.ZoneId >= 0) view.RemoveZone(Widget.ZoneId);

		_zoneView = null;
		Widget.ZoneId = -1;
		context.RefreshEditor();
	}

	public void Place(float left, float editorTop, float width, float editorHeight, Common.Render.ITextFont font, float originX, float originY) {
		if (!Widget.IsOpen || _zoneView is not { } view || workbench.ActiveView != view) {
			Widget.Visible = false;
			return;
		}

		float lineHeight = font.GetLineHeight();
		float height = Widget.ZoneHeight(lineHeight, editorHeight);

		if (view.Zones.FirstOrDefault(zone => zone.Id == Widget.ZoneId) is { } zone && zone.Height != height) {
			view.ResizeZone(Widget.ZoneId, height);
			context.RefreshEditor();
		}

		int line = Math.Clamp(Widget.Line - 1, 0, Math.Max(0, view.Snapshot.LineCount - 1));
		TextSpan span = view.Snapshot.GetLineSpan(line);
		int column = Math.Max(2, Widget.Column);
		int offset = Math.Min(span.Start + column - 1, span.End);

		Widget.LineHeight = lineHeight;
		Widget.ArrowX = view.GutterWidth(font) + TextView.TextPaddingLeft + (view.VisualColumnAt(line, offset) * font.GetCellWidth()) - view.ScrollX;
		Widget.Visible = true;
		Widget.SetBounds(left - originX, editorTop + view.ZoneTop(Widget.ZoneId, lineHeight) - view.ScrollY - originY, width, height);
	}
}
