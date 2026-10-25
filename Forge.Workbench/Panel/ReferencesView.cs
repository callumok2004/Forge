using Forge.Common.Commands;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ReferencesView : Panel
{
	private readonly PanelPart _part;
	private readonly ListPanel _list;

	public ReferencesView(PanelPart part) {
		_part = part;
		Name = "References";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_list = new ListPanel { Name = "ReferencesList", Dock = DockType.Fill, RowPainter = PaintRow, Activated = Activate };
		AddChild(_list);
	}

	public void Sync() {
		_list.SetCount(_part.References.Count);
		_list.ClearSelection();
	}

	private void Activate(int index) {
		if (index < 0 || index >= _part.References.Count) return;

		HostLocation location = _part.References[index];
		_part.Navigate?.Invoke(location.Path, location.Line, location.Column);
	}

	private void PaintRow(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		HostLocation location = _part.References[index];
		ITextFont font = painter.GetUi();
		float scale = Scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float cursor = left + (Metrics.Space12 * scale);

		Span<char> where = stackalloc char[300];
		ReadOnlySpan<char> name = Path.GetFileName(location.Path.AsSpan());
		name = name[..Math.Min(name.Length, 280)];
		name.CopyTo(where);
		where[name.Length] = ':';
		location.Line.TryFormat(where[(name.Length + 1)..], out int digits);
		ReadOnlySpan<char> label = where[..(name.Length + 1 + digits)];

		painter.DrawText(font, label, cursor, baseline, Theme.SectionHeaderForeground);
		cursor += font.Measure(label) + (Metrics.Space12 * scale);

		painter.DrawText(font, location.Preview, cursor, baseline, Theme.EditorForeground);
	}
}
