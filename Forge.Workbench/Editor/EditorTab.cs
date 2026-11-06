using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;
using Forge.Common.Editor;

namespace Forge.Workbench;

public sealed class EditorTab : Button
{
	private const float PaddingLeft = 6f;
	private const float PaddingRight = 8f;
	private const float DirtyPaddingRight = 28f;
	private const float DirtyActionSize = 20f;
	private const float LabelGap = 6f;
	private const float FillInset = 2f;
	private const float CapRadius = 4f;
	private const float ShoulderRadius = 7f;
	private const float LabelSize = 13f;

	private readonly TabStrip _strip;
	private bool _middlePressed;
	private bool _dirty;
	private IDocument? _document;
	private Codicon? _icon;

	public EditorTab(TabStrip strip) {
		FontSize = LabelSize;
		Radius = Metrics.RadiusSmall;
		Dock = DockType.Left;
		Padding = new Thickness(PaddingLeft + Metrics.IconSize + LabelGap, 0f, PaddingRight, 0f);
		Foreground = Theme.TabInactiveForeground;
		Action = () => strip.Activated?.Invoke(Index);
		_strip = strip;
	}

	public int Index { get; private set; }

	public bool Selected {
		get;
		private set {
			if (field == value) return;

			field = value;
			Repaint();
		}
	}

	public void Bind(IDocument document, int index, bool selected, Codicon? icon = null) {
		Index = index;
		Selected = selected;
		_icon = icon;
		bool dirty = document.IsDirty();

		if (dirty != _dirty) {
			_dirty = dirty;
			Padding = new Thickness(PaddingLeft + Metrics.IconSize + LabelGap, 0f, dirty ? DirtyPaddingRight : PaddingRight, 0f);
			Parent?.InvalidateLayout();
			Repaint();
		}

		if (ReferenceEquals(_document, document) && Text.SequenceEqual(document.GetName())) return;

		_document = document;
		SetText(document.GetName());
		Parent?.InvalidateLayout();
	}

	public float PreferredWidth(float scale) {
		if (Ui is null) return 0f;

		return MathF.Ceiling(MeasureContents(FontFor(Ui.GetFonts()), scale) + ((Padding.Left + Padding.Right) * scale));
	}

	public override bool OnMousePressed(MouseButton button, float x, float y) {
		if (button == MouseButton.Right) {
			_strip.ContextMenu?.Invoke(Index, ScreenX + x, ScreenY + y);
			return true;
		}

		if (button != MouseButton.Middle) return base.OnMousePressed(button, x, y);

		_middlePressed = true;
		Ui?.SetMouseCapture(this);
		return true;
	}

	public override bool OnMouseReleased(MouseButton button, float x, float y) {
		if (button != MouseButton.Middle || !_middlePressed) return base.OnMouseReleased(button, x, y);

		_middlePressed = false;
		Ui?.SetMouseCapture(null);

		if (x >= 0f && x < Wide && y >= 0f && y < Tall) _strip.Closed?.Invoke(Index);

		return true;
	}

	public override void OnCursorEntered() {
		base.OnCursorEntered();
		Repaint();
	}

	public override void OnCursorExited() {
		base.OnCursorExited();
		Repaint();
	}

	private Color LabelColor => Selected ? Theme.TabActiveForeground : IsCursorOver ? Theme.TabHoverForeground : Theme.TabInactiveForeground;

	public override void PaintBackground(IPainter painter) {
		float scale = Scale;
		float stroke = scale;

		if (!Selected) {
			if (!IsCursorOver) return;

			float inset = FillInset * scale;
			painter.FillRoundedRectangle(ScreenX + inset, ScreenY + inset, Wide - (inset * 2f), Tall - (inset * 2f), Radius * scale, Theme.TabHoverFill);
			return;
		}

		float top = ScreenY;
		float bottom = ScreenY + Tall + stroke;
		float cap = CapRadius * scale;
		float shoulder = ShoulderRadius * scale;
		float left = ScreenX;
		float right = ScreenX + Wide;

		painter.PushClip(left - shoulder, top, Wide + (shoulder * 2f), bottom - top);

		if (Index > 0) PaintShoulder(painter, left - shoulder, bottom - shoulder, shoulder, stroke, leftSide: true);

		PaintShoulder(painter, right, bottom - shoulder, shoulder, stroke, leftSide: false);
		painter.PopClip();

		painter.PushClip(left, top, Wide, bottom - top);
		painter.FillRoundedRectangle(left, top, Wide, (bottom - top) + cap, cap, Theme.EditorBackground);
		painter.PopClip();

		painter.PushClip(left, top, Wide, bottom - shoulder - top);
		painter.StrokeRoundedRectangle(left + (stroke / 2f), top + (stroke / 2f), Wide - stroke, (bottom - top) + cap, cap, stroke, Theme.TabsBorder);
		painter.PopClip();
	}

	private static void PaintShoulder(IPainter painter, float x, float y, float size, float stroke, bool leftSide) {
		painter.PushClip(x, y, size, size);
		painter.FillRectangle(x, y, size, size, Theme.EditorBackground);

		float circleX = leftSide ? x - size : x;
		painter.FillRoundedRectangle(circleX, y - size, size * 2f, size * 2f, size, Theme.TabsBackground);
		painter.StrokeRoundedRectangle(circleX + (stroke / 2f), y - size + (stroke / 2f), (size * 2f) - stroke, (size * 2f) - stroke, size - (stroke / 2f), stroke, Theme.TabsBorder);
		painter.PopClip();
	}

	public override void Paint(IPainter painter) {
		if (_document is not { } document) return;

		float scale = Scale;
		float iconSize = Metrics.IconSize * scale;
		float iconCentre = ScreenX + (PaddingLeft * scale) + (iconSize / 2f);
		float centreY = ScreenY + (Tall / 2f);
		Color color = LabelColor;

		string name = document.GetName();

		if (_icon is { } icon) painter.DrawIcon(icon, iconCentre, centreY, iconSize, color);
		else if (!painter.DrawFileIcon(name, iconCentre, centreY, iconSize)) painter.DrawIcon(FileIcon(name), iconCentre, centreY, iconSize, FileTypeChip(name, Selected));

		PaintContents(painter, color);

		if (_dirty) painter.DrawIcon(Codicon.CircleFilled, ScreenX + Wide - ((PaddingRight + (DirtyActionSize / 2f)) * scale), centreY, iconSize, color);
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
