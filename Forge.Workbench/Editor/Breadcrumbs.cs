using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class Breadcrumbs : Panel
{
	private const int MaxSegments = 6;

	private const float FileIconGap = 6f;

	private readonly Label[] _segments = new Label[MaxSegments];
	private string? _fileName;
	private int _fileSegment = -1;

	public Breadcrumbs() {
		Name = "Breadcrumbs";
		Dock = DockType.Top;
		MouseInputEnabled = false;

		for (int i = 0; i < MaxSegments; i++) {
			_segments[i] = new Label { Name = $"Segment{i}", Dock = DockType.Left, Visible = false, SizeToContents = true, Foreground = Theme.BreadcrumbForeground, IconColor = static _ => Theme.BreadcrumbForeground, Padding = new Thickness(0f, 0f, Metrics.Space4, 0f) };
			AddChild(_segments[i]);
		}
	}

	public static float Height(float scale) => Metrics.BreadcrumbHeight * scale;

	public void SetPath(string? rootPath, string? fullPath) {
		Span<Range> parts = stackalloc Range[32];
		int count = 0;
		ReadOnlySpan<char> relative = string.IsNullOrEmpty(fullPath) ? default : string.IsNullOrEmpty(rootPath) ? Path.GetFileName(fullPath.AsSpan()) : Path.GetRelativePath(rootPath, fullPath).AsSpan();

		if (!relative.IsEmpty) count = relative.SplitAny(parts, ['/', '\\']);

		int first = Math.Max(0, count - MaxSegments);
		int shown = count - first;
		_fileName = shown > 0 ? Path.GetFileName(fullPath) : null;
		_fileSegment = shown - 1;
		Span<char> buffer = stackalloc char[512];

		for (int i = 0; i < MaxSegments; i++) {
			Label label = _segments[i];
			label.Visible = i < shown;

			if (i >= shown) continue;

			ReadOnlySpan<char> name = relative[parts[first + i]];
			bool last = i == shown - 1;
			int length = 0;

			length += Write(buffer[length..], name[..Math.Min(name.Length, buffer.Length - 40)]);

			if (!last) length += Write(buffer[length..], " $(chevron-right)");

			label.Padding = new Thickness(last ? Metrics.IconSize + FileIconGap : 0f, 0f, Metrics.Space4, 0f);
			label.Foreground = last ? Theme.BreadcrumbActiveForeground : Theme.BreadcrumbForeground;
			label.SetText(buffer[..length]);
			label.Repaint();
		}
	}

	private static int Write(Span<char> destination, ReadOnlySpan<char> text) {
		text.CopyTo(destination);
		return text.Length;
	}

	public override void PerformLayout() {
		float scale = Scale;
		DockPadding = new Thickness(Metrics.SpaceMd * scale, 0f, 0f, 0f);

		foreach (Label segment in _segments) segment.FitContents();

		base.PerformLayout();
	}

	public override void PostChildPaint(IPainter painter) {
		if (_fileName is not { } name || _fileSegment < 0) return;

		Label label = _segments[_fileSegment];
		float size = Metrics.IconSize * Scale;
		float x = label.ScreenX + (size / 2f);
		float y = label.ScreenY + (label.Tall / 2f);

		if (!painter.DrawFileIcon(name, x, y, size)) painter.DrawIcon(Codicon.File, x, y, size, Theme.BreadcrumbForeground);
	}
}
