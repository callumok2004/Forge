using Forge.Common.Debug;
using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class ExceptionWidget : Panel
{
	private const float Padding = 8f;
	private const float BorderThickness = 2f;
	private const float ActionSize = 22f;
	private const float SectionGap = 6f;

	private static readonly Color Background = Color.Parse("#420b0d");
	private static readonly Color Border = Color.Parse("#a31515");

	private readonly ActionButton _copy = new() { Icon = Codicon.Copy };
	private readonly ActionButton _close = new() { Icon = Codicon.Close };
	private readonly List<(string Text, bool IsCode)> _lines = [];

	private DebugExceptionInfo? _info;
	private float _laidOutWidth = -1f;

	public ExceptionWidget() {
		Name = "ExceptionWidget";
		Visible = false;

		_copy.Action = () => {
			if (_info is { } info) Clipboard.SetText(Describe(info));
		};

		_close.Action = Hide;

		AddChild(_copy);
		AddChild(_close);
	}

	public string? Path { get; private set; }

	public int Line { get; private set; }

	public int ZoneId { get; set; } = -1;

	public bool IsOpen => _info is not null;

	public Action? Closed { get; set; }

	public void Show(DebugExceptionInfo info, string path, int line) {
		_info = info;
		Path = path;
		Line = line;
		_laidOutWidth = -1f;
		Visible = true;
		Repaint();
	}

	public void Hide() {
		if (_info is null) return;

		_info = null;
		Visible = false;
		Repaint();
		Closed?.Invoke();
	}

	private static string Title(DebugExceptionInfo info) => info.ExceptionId.Length > 0 ? $"Exception has occurred: {info.ExceptionId}" : "Exception has occurred.";

	private static string Describe(DebugExceptionInfo info) => string.Join(Environment.NewLine, new[] { Title(info), info.Description, info.StackTrace }.Where(static part => part is { Length: > 0 }));

	public float Measure(float width) {
		if (_info is not { } info || Ui is null) return 0f;

		float scale = Scale;
		IFontSet fonts = Ui.GetFonts();
		float inner = width - (Padding * 2f * scale);

		if (width != _laidOutWidth) {
			_laidOutWidth = width;
			_lines.Clear();

			if (info.Description is { Length: > 0 } description) Wrap(fonts.GetUi(), description, inner, isCode: false);

			if (info.StackTrace is { Length: > 0 } stack) Wrap(fonts.GetCode(), stack, inner, isCode: true);
		}

		float height = (BorderThickness * 2f * scale) + (Padding * scale) + (ActionSize * scale) + (Padding * scale);

		foreach ((_, bool isCode) in _lines) height += (isCode ? fonts.GetCode() : fonts.GetUi()).GetLineHeight();

		if (_lines.Count > 0) height += SectionGap * scale;

		return height;
	}

	private void Wrap(ITextFont font, string text, float width, bool isCode) {
		foreach (string raw in text.ReplaceLineEndings("\n").Split('\n')) {
			string line = raw.TrimEnd();

			while (line.Length > 0 && font.Measure(line) > width) {
				int cut = line.Length;

				while (cut > 1 && font.Measure(line.AsSpan(0, cut)) > width) cut--;

				int space = isCode ? -1 : line.LastIndexOf(' ', cut - 1);
				int split = space > 0 ? space : cut;

				_lines.Add((line[..split], isCode));
				line = line[split..].TrimStart();
			}

			_lines.Add((line, isCode));
		}
	}

	public override void PerformLayout() {
		float scale = Scale;
		float size = ActionSize * scale;
		float top = (BorderThickness + Padding) * scale;

		_close.SetBounds(Wide - (Padding * scale) - size, top, size, size);
		_copy.SetBounds(Wide - (Padding * scale) - (size * 2f) - (2f * scale), top, size, size);
	}

	public override void Paint(IPainter painter) {
		if (_info is not { } info) return;

		float scale = Scale;
		float border = BorderThickness * scale;
		ITextFont ui = painter.GetUi();
		ITextFont code = painter.GetCode();
		float left = ScreenX + (Padding * scale);

		painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Background);
		painter.FillRectangle(ScreenX, ScreenY, Wide, border, Border);
		painter.FillRectangle(ScreenX, ScreenY + Tall - border, Wide, border, Border);

		float titleTop = ScreenY + border + (Padding * scale);
		float titleBaseline = titleTop + (((ActionSize * scale) - ui.GetLineHeight()) / 2f) + ui.GetBaseline();

		painter.DrawText(ui, Title(info), left, titleBaseline, Theme.Foreground);

		float y = titleTop + (ActionSize * scale) + (SectionGap * scale);

		foreach ((string text, bool isCode) in _lines) {
			ITextFont font = isCode ? code : ui;

			painter.DrawText(font, text, left, y + font.GetBaseline(), isCode ? Theme.DescriptionForeground : Theme.Foreground);
			y += font.GetLineHeight();
		}
	}

	public override bool OnKeyCodePressed(uint key, KeyModifiers modifiers) {
		if (key != VirtualKey.Escape) return false;

		Hide();
		return true;
	}
}
