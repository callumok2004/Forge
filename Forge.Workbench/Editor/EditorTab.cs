using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.Common.Editor;

namespace Forge.Workbench;

public sealed class EditorTab : Button
{
	private const float MinWidth = 110f;
	private const float MaxWidth = 240f;
	private const float CloseSlot = 24f;
	private const float CloseSize = 18f;

	private readonly TabCloseButton _close;
	private IDocument? _document;

	public EditorTab(TabStrip strip) {
		Font = FontRole.UiStrong;
		Radius = Metrics.RadiusSmall;
		Dock = DockType.Left;
		Padding = new Thickness(Metrics.Space8 + Metrics.IconSize + Metrics.Space6, 0f, CloseSlot, 0f);
		Action = () => strip.Activated?.Invoke(Index);

		_close = new TabCloseButton(this) { Action = () => strip.Closed?.Invoke(Index) };
		AddChild(_close);
	}

	public int Index { get; private set; }

	public bool Selected {
		get;
		private set {
			if (field == value) return;

			field = value;
			Foreground = value ? Theme.TabActiveForeground : Theme.TabInactiveForeground;
			Repaint();
		}
	}

	public bool Hovered => IsCursorOver || _close.IsCursorOver;

	public void Bind(IDocument document, int index, bool selected) {
		Index = index;
		Selected = selected;
		_close.Dirty = document.IsDirty();

		if (ReferenceEquals(_document, document) && Text.SequenceEqual(document.GetName())) return;

		_document = document;
		SetText(document.GetName());
		Parent?.InvalidateLayout();
	}

	public float PreferredWidth(float scale) {
		if (Ui is null) return MinWidth * scale;

		float width = MeasureContents(FontFor(Ui.GetFonts()), scale) + ((Padding.Left + Padding.Right) * scale);
		return Math.Clamp(width, MinWidth * scale, MaxWidth * scale);
	}

	public override void OnCursorEntered() {
		base.OnCursorEntered();
		_close.Repaint();
	}

	public override void OnCursorExited() {
		base.OnCursorExited();
		_close.Repaint();
	}

	public override void PerformLayout() {
		float scale = Scale;
		float size = CloseSize * scale;
		_close.SetBounds(Wide - (CloseSlot * scale) + ((8f - (CloseSize / 2f)) * scale), (Tall - size) / 2f, size, size);
	}

	public override void PaintBackground(IPainter painter) {
		float tint = Selected ? 0.18f : Hovered ? 0.08f : 0.05f;
		painter.FillRoundedRectangle(ScreenX, ScreenY, Wide, Tall, Radius * Scale, Theme.EditorForeground.WithAlpha(tint));
	}

	public override void Paint(IPainter painter) {
		if (_document is not { } document) return;

		float scale = Scale;
		float iconSize = Metrics.IconSize * scale;
		float iconCentre = ScreenX + (Metrics.Space8 * scale) + (iconSize / 2f);
		float centreY = ScreenY + (Tall / 2f);

		string name = document.GetName();

		if (!painter.DrawFileIcon(name, iconCentre, centreY, iconSize)) painter.DrawIcon(FileIcon(name), iconCentre, centreY, iconSize, FileTypeChip(name, Selected));

		PaintContents(painter, Foreground);
	}

	private static Codicon FileIcon(ReadOnlySpan<char> name) {
		ReadOnlySpan<char> extension = Path.GetExtension(name);

		if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return Codicon.Json;

		return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? Codicon.Markdown : Codicon.File;
	}

	private static Color FileTypeChip(ReadOnlySpan<char> name, bool active) {
		ReadOnlySpan<char> extension = Path.GetExtension(name);

		Color color =
			extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#B392F0") :
			extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#FFAB70") :
			extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#79B8FF") :
			extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#6BD68A") :
			extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ? Color.Parse("#F9E27D") :
			Theme.SectionHeaderForeground;

		return active ? color : color.WithAlpha(0.7f);
	}
}
