using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class BinaryFilePane : EditorPane
{
	private const string Message = "The file is not displayed in the text editor because it is either binary or uses an unsupported text encoding.";
	private const float IconSize = 48f;
	private const float Gap = 10f;
	private const float LabelSize = 14f;
	private const float LabelMaxWidth = 450f;
	private const float SidePadding = 16f;
	private const float ButtonHeight = 26f;
	private const float ButtonPadding = 10f;
	private const float HideIconBelow = 200f;

	private readonly string _path;
	private readonly Button _openAnyway;
	private readonly List<Range> _lines = [];
	private float _wrappedWidth = -1f;
	private float _lineHeight;

	public BinaryFilePane(string path, Action<string> openAnyway) {
		_path = path;
		Id = path;
		Name = "BinaryFile";
		Dock = DockType.Fill;

		_openAnyway = new Button {
			Name = "OpenAnyway",
			Background = Theme.ButtonBackground,
			HoverBackground = Theme.ButtonHoverBackground,
			Foreground = Theme.ButtonForeground,
			Radius = Metrics.RadiusSmall,
			Padding = new Thickness(ButtonPadding, 0f, ButtonPadding, 0f),
			SizeToContents = true,
			Action = () => openAnyway(path),
		};
		_openAnyway.SetText("Open Anyway");
		AddChild(_openAnyway);
	}

	public override string Title => Path.GetFileName(_path);

	public override Codicon Icon => Codicon.File;

	private bool ShowIcon => Tall >= HideIconBelow * Scale;

	private void Wrap(ITextFont font, float width) {
		if (width == _wrappedWidth) return;

		_wrappedWidth = width;
		_lines.Clear();

		ReadOnlySpan<char> text = Message;
		int start = 0;

		while (start < text.Length) {
			int end = start;
			int lastBreak = -1;

			while (end < text.Length) {
				int next = text[end..].IndexOf(' ');
				int wordEnd = next < 0 ? text.Length : end + next;

				if (font.Measure(text[start..wordEnd]) > width && lastBreak > start) break;

				lastBreak = wordEnd;
				end = wordEnd + 1;
			}

			int stop = Math.Min(lastBreak < 0 ? text.Length : lastBreak, text.Length);
			_lines.Add(new Range(start, stop));
			start = stop + 1;
		}
	}

	private float ContentHeight(float scale) => (ShowIcon ? (IconSize + Gap) * scale : 0f) + (_lines.Count * _lineHeight) + (Gap * scale) + (ButtonHeight * scale);

	public override void PerformLayout() {
		float scale = Scale;

		if (Ui is null) return;

		ITextFont font = Ui.GetFonts().GetUi(LabelSize, false);
		_lineHeight = font.GetLineHeight();
		Wrap(font, Math.Min(LabelMaxWidth * scale, Math.Max(1f, Wide - (SidePadding * 2f * scale))));

		_openAnyway.FitContents();
		float top = (Tall - ContentHeight(scale)) / 2f;
		float buttonTop = top + ContentHeight(scale) - (ButtonHeight * scale);
		_openAnyway.SetBounds(MathF.Round((Wide - _openAnyway.Wide) / 2f), MathF.Round(buttonTop), _openAnyway.Wide, ButtonHeight * scale);
	}

	public override void Paint(IPainter painter) {
		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.EditorBackground);

		if (Ui is null) return;

		float scale = Scale;
		ITextFont font = Ui.GetFonts().GetUi(LabelSize, false);
		float y = ScreenY + ((Tall - ContentHeight(scale)) / 2f);

		if (ShowIcon) {
			float icon = IconSize * scale;
			painter.DrawIcon(Codicon.Warning, ScreenX + (Wide / 2f), y + (icon / 2f), icon, Theme.WarningForeground);
			y += icon + (Gap * scale);
		}

		ReadOnlySpan<char> text = Message;

		foreach (Range line in _lines) {
			ReadOnlySpan<char> part = text[line];
			painter.DrawText(font, part, ScreenX + ((Wide - font.Measure(part)) / 2f), y + font.GetBaseline(), Theme.EditorForeground);
			y += _lineHeight;
		}
	}
}
