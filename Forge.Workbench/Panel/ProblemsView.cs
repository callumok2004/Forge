using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ProblemsView : Panel
{
	private readonly PanelPart _part;
	private readonly ListPanel _list;
	private readonly Label _empty;

	public ProblemsView(PanelPart part) {
		_part = part;
		Name = "Problems";
		Dock = DockType.Fill;
		MouseInputEnabled = false;

		_empty = new Label("No problems have been detected in the workspace.") { Name = "ProblemsEmpty", Dock = DockType.Top, Foreground = Theme.SectionHeaderForeground, Padding = new Thickness(Metrics.Space12, 0f, 0f, 0f) };
		_list = new ListPanel { Name = "ProblemsList", Dock = DockType.Fill, RowPainter = PaintRow, Activated = Activate };

		AddChild(_empty);
		AddChild(_list);
		Sync();
	}

	public void Sync() {
		int count = _part.Problems.Count;
		_list.SetCount(count);
		_list.ClearSelection();
		_list.Visible = count > 0;
		_empty.Visible = count == 0;
	}

	private void Activate(int index) {
		if (index < 0 || index >= _part.Problems.Count) return;

		Problem problem = _part.Problems[index];
		_part.Navigate?.Invoke(problem.File, problem.Line, problem.Column);
	}

	private void PaintRow(IPainter painter, int index, float left, float top, float width, float height, bool selected) {
		Problem problem = _part.Problems[index];
		ITextFont font = painter.GetUi();
		float scale = Scale;
		float baseline = top + ((height - font.GetLineHeight()) / 2f) + font.GetBaseline();
		float cursor = left + (Metrics.Space12 * scale);

		painter.DrawIcon(problem.IsError ? Codicon.Error : Codicon.Warning, cursor + (Metrics.Space8 * scale), top + (height / 2f), Metrics.IconSize * scale, problem.IsError ? Theme.ErrorForeground : Theme.WarningForeground);
		cursor += (Metrics.Space16 + Metrics.Space8) * scale;

		painter.DrawText(font, problem.Message, cursor, baseline, Theme.EditorForeground);
		cursor += font.Measure(problem.Message) + (Metrics.Space12 * scale);

		Span<char> location = stackalloc char[300];
		ReadOnlySpan<char> file = problem.File.AsSpan(0, Math.Min(problem.File.Length, 280));
		file.CopyTo(location);
		location[file.Length] = ':';
		problem.Line.TryFormat(location[(file.Length + 1)..], out int digits);

		painter.DrawText(font, location[..(file.Length + 1 + digits)], cursor, baseline, Theme.SectionHeaderForeground);
	}

	public override void PerformLayout() {
		_empty.SetSize(Wide, (Metrics.Space12 * 2f * Scale) + (Metrics.TreeRowHeight * Scale));
		base.PerformLayout();
	}
}
