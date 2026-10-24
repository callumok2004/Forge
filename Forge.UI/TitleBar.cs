using Forge.Common.Render;
using Forge.Core.Native;
using Forge.Theming;

namespace Forge.UI;

public enum TitleBarPart
{
	None,
	AppIcon,
	Menu,
	Project,
	Branch,
	Account,
	CommandCentre,
	RunConfiguration,
	Run,
	Debug,
	Continue,
	Pause,
	Stop,
	Restart,
	ShowNextStatement,
	StepInto,
	StepOver,
	StepOut,
	Minimize,
	Maximize,
	Close,
}

public sealed class TitleBar
{
	private const float CaptionButtonWidth = 46f;
	private const float WidgetHeight = 26f;
	private const float AppIconSize = 20f;
	private const float RunButtonWidth = 28f;
	private const float CommandCentreHeight = 22f;
	private const float CommandCentreMaxWidth = 600f;
	private const float CommandCentreWidthFraction = 0.38f;
	private const float SideFraction = 0.2f;
	private const float ContainerMargin = 10f;
	private const float ItemMargin = 6f;
	private const float CommandCentrePadding = 12f;
	private const int MaxRegions = 24;

	private const string MenuLabel = "Forge";
	private const string SignInLabel = "Sign in";

	private readonly Region[] _regions = new Region[MaxRegions];
	private int _regionCount;

	private string? _colouredName;
	private Color _projectColor;
	private string _initials = string.Empty;

	private readonly record struct Region(TitleBarPart Part, float Left, float Right);

	public static float Height(float scale) => Metrics.TitleBarHeight * scale;

	public string Title { get; set; } = "Forge";

	public string? FolderName { get; set; }

	public string? BranchName { get; set; }

	public string? RemoteOwner { get; set; }

	public string? RemoteRepository { get; set; }

	public string? AccountName { get; set; }

	public IReadOnlyList<string> DebugConfigurations { get; set; } = [];

	public string? SelectedDebugConfiguration { get; set; }

	public bool IsDebugging { get; set; }

	public bool IsPaused { get; set; }

	public bool IsMaximized { get; set; }

	public bool MenuOpen { get; set; }

	public IImage? Icon { get; set; }

	public TitleBarPart Hover { get; private set; }

	public float MenuLeft { get; private set; }

	public float ProjectLeft { get; private set; }

	public float RunConfigurationLeft { get; private set; }

	public string? ProjectName => RemoteRepository is { Length: > 0 } repository ? repository : FolderName;

	public void SetHover(TitleBarPart part) => Hover = part;

	public TitleBarPart PartAt(float x, float y, float windowWidth, float scale) {
		if (y < 0f || y >= Height(scale)) return TitleBarPart.None;

		float button = CaptionButtonWidth * scale;
		float fromRight = windowWidth - x;

		if (fromRight >= 0f && fromRight <= button) return TitleBarPart.Close;

		if (fromRight > button && fromRight <= button * 2f) return TitleBarPart.Maximize;

		if (fromRight > button * 2f && fromRight <= button * 3f) return TitleBarPart.Minimize;

		for (int i = 0; i < _regionCount; i++)
			if (x >= _regions[i].Left && x < _regions[i].Right) return _regions[i].Part;

		return TitleBarPart.None;
	}

	public string? CommandAt(float x, float y, float windowWidth, float scale) => CommandFor(PartAt(x, y, windowWidth, scale));

	public static string? CommandFor(TitleBarPart part) => part switch {
		TitleBarPart.Menu => "forge.titleBar.openMenu",
		TitleBarPart.Project => "forge.titleBar.openProjectMenu",
		TitleBarPart.Branch => "git.checkout",
		TitleBarPart.CommandCentre => "workbench.action.quickOpen",
		TitleBarPart.RunConfiguration => "forge.debug.pickConfiguration",
		TitleBarPart.Run => "workbench.action.debug.run",
		TitleBarPart.Debug => "workbench.action.debug.start",
		TitleBarPart.Continue => "workbench.action.debug.continue",
		TitleBarPart.Pause => "workbench.action.debug.pause",
		TitleBarPart.Stop => "workbench.action.debug.stop",
		TitleBarPart.Restart => "workbench.action.debug.restart",
		TitleBarPart.ShowNextStatement => "workbench.action.debug.callStackTop",
		TitleBarPart.StepInto => "workbench.action.debug.stepInto",
		TitleBarPart.StepOver => "workbench.action.debug.stepOver",
		TitleBarPart.StepOut => "workbench.action.debug.stepOut",
		_ => null,
	};

	public nint HitTest(float x, float y, float windowWidth, float scale) => PartAt(x, y, windowWidth, scale) switch {
		TitleBarPart.Minimize => Win32.HtMinButton,
		TitleBarPart.Maximize => Win32.HtMaxButton,
		TitleBarPart.Close => Win32.HtClose,
		TitleBarPart.None => y < Height(scale) ? Win32.HtCaption : Win32.HtClient,
		_ => Win32.HtClient,
	};

	public void Render(IPainter painter, float width, float scale) {
		float height = Height(scale);
		float centreY = height / 2f;

		_regionCount = 0;

		painter.FillRectangle(0f, 0f, width, height, Theme.TitleBarBackground);

		float left = DrawLeft(painter, centreY, scale);
		float right = DrawRunCluster(painter, width - (CaptionButtonWidth * 3f * scale) - (Metrics.Space8 * scale), centreY, scale);

		DrawCommandCentre(painter, width, left, right, centreY, scale);
		DrawCaptionButtons(painter, width, height, scale);
	}

	private float DrawLeft(IPainter painter, float centreY, float scale) {
		float slot = ActivityBar.Width(scale);

		if (Icon is { } icon) painter.DrawImage(icon, ActivityBar.CentreX(scale), centreY, AppIconSize * scale);

		AddRegion(TitleBarPart.AppIcon, 0f, slot);

		float cursor = slot + (Metrics.Space4 * scale);

		cursor = DrawMenuButton(painter, cursor, centreY, scale);

		if (ProjectName is { Length: > 0 } project) cursor = DrawProject(painter, project, cursor + (Metrics.Space16 * scale), centreY, scale);

		if (BranchName is { Length: > 0 } branch) cursor = DrawBranch(painter, branch, cursor + (Metrics.Space2 * scale), centreY, scale);

		return DrawAccount(painter, cursor + (Metrics.Space2 * scale), centreY, scale);
	}

	private float DrawMenuButton(IPainter painter, float left, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		float width = font.Measure(MenuLabel) + (Metrics.Space8 * 2f * scale);

		MenuLeft = left;

		if (MenuOpen || Hover == TitleBarPart.Menu) FillWidget(painter, left, width, centreY, scale, Theme.MenubarSelectionBackground);

		painter.DrawText(font, MenuLabel, left + (Metrics.Space8 * scale), Baseline(font, centreY), Theme.TitleBarForeground);

		AddRegion(TitleBarPart.Menu, left, left + width);
		return left + width;
	}

	private float DrawProject(IPainter painter, string name, float left, float centreY, float scale) {
		EnsureProjectColour(name);

		ITextFont font = painter.GetUiStrong();
		float padding = Metrics.Space6 * scale;
		float gap = Metrics.Space6 * scale;
		float tile = 20f * scale;
		float chevron = 12f * scale;
		float labelWidth = Math.Min(font.Measure(name), 220f * scale);
		float width = padding + tile + gap + labelWidth + gap + chevron - (Metrics.Space2 * scale) + padding;

		ProjectLeft = left;

		if (Hover == TitleBarPart.Project) FillWidget(painter, left, width, centreY, scale, Theme.ToolbarHoverBackground);

		float tileLeft = left + padding;
		float tileTop = centreY - (tile / 2f);
		float radius = Metrics.RadiusMedium * scale;

		painter.FillRoundedRectangle(tileLeft, tileTop, tile, tile, radius, _projectColor);
		painter.StrokeRoundedRectangle(tileLeft, tileTop, tile, tile, radius, Metrics.StrokeThickness * scale, Theme.TitleBarForeground.WithAlpha(0.1f));

		ITextFont small = painter.GetUiSmall();
		painter.DrawText(small, _initials, tileLeft + ((tile - small.Measure(_initials)) / 2f), Baseline(small, centreY), Color.Parse("#ffffff"));

		float cursor = tileLeft + tile + gap;

		painter.PushClip(cursor, tileTop, labelWidth, tile);
		painter.DrawText(font, name, cursor, Baseline(font, centreY), Theme.TitleBarForeground);
		painter.PopClip();

		cursor += labelWidth + gap - (Metrics.Space2 * scale);
		painter.DrawIcon(Codicon.ChevronDown, cursor + (chevron / 2f), centreY, chevron, Theme.TitleBarForeground.WithAlpha(0.55f));

		AddRegion(TitleBarPart.Project, left, left + width);
		return left + width;
	}

	private float DrawBranch(IPainter painter, string branch, float left, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		float padding = Metrics.Space6 * scale;
		float gap = Metrics.Space6 * scale;
		float icon = 14f * scale;
		float chevron = 12f * scale;
		float labelWidth = Math.Min(font.Measure(branch), 260f * scale);
		float width = padding + icon + gap + labelWidth + gap + chevron - (Metrics.Space2 * scale) + padding;

		if (Hover == TitleBarPart.Branch) FillWidget(painter, left, width, centreY, scale, Theme.ToolbarHoverBackground);

		float cursor = left + padding;
		painter.DrawIcon(Codicon.GitBranch, cursor + (icon / 2f), centreY, icon, Theme.TitleBarForeground.WithAlpha(0.8f));
		cursor += icon + gap;

		painter.PushClip(cursor, centreY - (WidgetHeight * scale / 2f), labelWidth, WidgetHeight * scale);
		painter.DrawText(font, branch, cursor, Baseline(font, centreY), Theme.TitleBarForeground);
		painter.PopClip();

		cursor += labelWidth + gap - (Metrics.Space2 * scale);
		painter.DrawIcon(Codicon.ChevronDown, cursor + (chevron / 2f), centreY, chevron, Theme.TitleBarForeground.WithAlpha(0.55f));

		AddRegion(TitleBarPart.Branch, left, left + width);
		return left + width;
	}

	private float DrawAccount(IPainter painter, float left, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		string label = AccountName is { Length: > 0 } name ? name : SignInLabel;
		float avatar = 20f * scale;
		float gap = Metrics.Space6 * scale;
		float labelWidth = Math.Min(font.Measure(label), 140f * scale);
		float width = (Metrics.Space4 * scale) + avatar + gap + labelWidth + (Metrics.Space8 * scale);

		if (Hover == TitleBarPart.Account) FillWidget(painter, left, width, centreY, scale, Theme.ToolbarHoverBackground);

		float cursor = left + (Metrics.Space4 * scale);
		painter.DrawIcon(Codicon.Account, cursor + (avatar / 2f), centreY, 16f * scale, Theme.TitleBarForeground);
		cursor += avatar + gap;

		painter.PushClip(cursor, centreY - avatar, labelWidth, avatar * 2f);
		painter.DrawText(font, label, cursor, Baseline(font, centreY), Theme.TitleBarForeground);
		painter.PopClip();

		AddRegion(TitleBarPart.Account, left, left + width);
		return left + width;
	}

	private float DrawRunCluster(IPainter painter, float right, float centreY, float scale) {
		RunConfigurationLeft = 0f;

		if (DebugConfigurations.Count == 0 && !IsDebugging) return right;

		ITextFont font = painter.GetUi();
		string label = SelectedDebugConfiguration ?? (DebugConfigurations.Count > 0 ? DebugConfigurations[0] : "Add Configuration...");

		float button = RunButtonWidth * scale;
		float gap = Metrics.Space2 * scale;
		float separator = (Metrics.Space6 * 2f * scale) + scale;
		float dot = 7f * scale;
		float chevron = 12f * scale;
		float labelWidth = Math.Min(font.Measure(label), 220f * scale);
		bool showDot = IsDebugging;
		float configWidth = (Metrics.Space8 * scale) + (showDot ? dot + (Metrics.Space6 * scale) : 0f) + labelWidth + (Metrics.Space6 * scale) + chevron - (Metrics.Space2 * scale) + (Metrics.Space6 * scale);

		ReadOnlySpan<TitleBarPart> runGroup = IsDebugging ? [IsPaused ? TitleBarPart.Continue : TitleBarPart.Pause, TitleBarPart.Stop, TitleBarPart.Restart] : [TitleBarPart.Run, TitleBarPart.Debug];
		ReadOnlySpan<TitleBarPart> stepGroup = IsDebugging ? [TitleBarPart.ShowNextStatement, TitleBarPart.StepInto, TitleBarPart.StepOver, TitleBarPart.StepOut] : [];

		float total = configWidth + gap + separator + GroupWidth(runGroup.Length, button, gap) + (stepGroup.Length > 0 ? separator + GroupWidth(stepGroup.Length, button, gap) : 0f);
		float cursor = right - total;
		float clusterLeft = cursor - (Metrics.Space4 * scale);

		RunConfigurationLeft = cursor;

		Color tint = IsPaused ? Theme.DebugIconPause : Theme.DebugIconStart;

		if (IsDebugging) FillWidget(painter, cursor, configWidth, centreY, scale, tint.WithAlpha(0.16f));
		else if (Hover == TitleBarPart.RunConfiguration) FillWidget(painter, cursor, configWidth, centreY, scale, Theme.ToolbarHoverBackground);

		float text = cursor + (Metrics.Space8 * scale);

		if (showDot) {
			painter.FillCircle(text + (dot / 2f), centreY, dot, tint.WithAlpha(0.25f));
			painter.FillCircle(text + (dot / 2f), centreY, dot / 2f, tint);
			text += dot + (Metrics.Space6 * scale);
		}

		painter.PushClip(text, centreY - (WidgetHeight * scale / 2f), labelWidth, WidgetHeight * scale);
		painter.DrawText(font, label, text, Baseline(font, centreY), Theme.TitleBarForeground);
		painter.PopClip();

		text += labelWidth + (Metrics.Space6 * scale) - (Metrics.Space2 * scale);
		painter.DrawIcon(Codicon.ChevronDown, text + (chevron / 2f), centreY, chevron, Theme.TitleBarForeground.WithAlpha(0.55f));

		AddRegion(TitleBarPart.RunConfiguration, cursor, cursor + configWidth);
		cursor += configWidth + gap;

		cursor = DrawSeparator(painter, cursor, centreY, scale);
		cursor = DrawRunGroup(painter, runGroup, cursor, centreY, button, gap, scale);

		if (stepGroup.Length > 0) {
			cursor = DrawSeparator(painter, cursor - gap, centreY, scale);
			DrawRunGroup(painter, stepGroup, cursor, centreY, button, gap, scale);
		}

		return clusterLeft;
	}

	private static float GroupWidth(int count, float button, float gap) => (button * count) + (gap * count);

	private float DrawSeparator(IPainter painter, float left, float centreY, float scale) {
		float x = left + (Metrics.Space6 * scale);
		painter.DrawVerticalLine(x, centreY - (8f * scale), centreY + (8f * scale), Theme.TitleBarForeground.WithAlpha(0.18f));
		return x + scale + (Metrics.Space6 * scale);
	}

	private float DrawRunGroup(IPainter painter, ReadOnlySpan<TitleBarPart> parts, float left, float centreY, float button, float gap, float scale) {
		foreach (TitleBarPart part in parts) {
			bool enabled = part is not (TitleBarPart.ShowNextStatement or TitleBarPart.StepInto or TitleBarPart.StepOver or TitleBarPart.StepOut) || IsPaused;

			if (enabled && Hover == part) FillWidget(painter, left, button, centreY, scale, Theme.ToolbarHoverBackground);

			(Codicon icon, Color color) = RunIcon(part);
			painter.DrawIcon(icon, left + (button / 2f), centreY, Metrics.IconSize * scale, enabled ? color : color.WithAlpha(0.35f));

			if (enabled) AddRegion(part, left, left + button);

			left += button + gap;
		}

		return left;
	}

	private static (Codicon Icon, Color Color) RunIcon(TitleBarPart part) => part switch {
		TitleBarPart.Run => (Codicon.DebugStart, Theme.DebugIconStart),
		TitleBarPart.Debug => (Codicon.DebugAlt, Theme.DebugIconStart),
		TitleBarPart.Continue => (Codicon.DebugContinue, Theme.DebugIconStart),
		TitleBarPart.Pause => (Codicon.DebugPause, Theme.DebugIconPause),
		TitleBarPart.Stop => (Codicon.DebugStop, Theme.DebugIconStop),
		TitleBarPart.Restart => (Codicon.DebugRestart, Theme.DebugIconRestart),
		TitleBarPart.ShowNextStatement => (Codicon.DebugStackframe, Theme.DebugIconPause),
		TitleBarPart.StepInto => (Codicon.DebugStepInto, Theme.DebugIconStep),
		TitleBarPart.StepOver => (Codicon.DebugStepOver, Theme.DebugIconStep),
		_ => (Codicon.DebugStepOut, Theme.DebugIconStep),
	};

	private void DrawCommandCentre(IPainter painter, float windowWidth, float leftEnd, float rightStart, float centreY, float scale) {
		ITextFont font = painter.GetUi();
		string label = Title;

		float leftBox = Math.Max(windowWidth * SideFraction, leftEnd);
		float rightBox = Math.Max(windowWidth * SideFraction, windowWidth - rightStart);
		float available = Math.Max(windowWidth - leftBox - rightBox, 0f);

		float inset = (ContainerMargin + ItemMargin) * scale;
		float content = (CommandCentrePadding * 2f * scale) + (20f * scale) + font.Measure(label);
		float preferred = Math.Min(content, Math.Min(windowWidth * CommandCentreWidthFraction, CommandCentreMaxWidth * scale)) + (inset * 2f);
		float outer = Math.Min(preferred, available);
		float width = outer - (inset * 2f);

		if (width <= 0f) return;

		float left = leftBox + ((available - outer) / 2f) + inset;
		float height = CommandCentreHeight * scale;
		float top = centreY - (height / 2f);
		float radius = Metrics.RadiusMedium * scale;
		bool hovered = Hover == TitleBarPart.CommandCentre;

		if (hovered) painter.FillRoundedRectangle(left, top, width, height, radius, Theme.CommandCentreHoverBackground);

		painter.StrokeRoundedRectangle(left, top, width, height, radius, Metrics.StrokeThickness * scale, hovered ? Theme.CommandCentreActiveBorder : Theme.CommandCentreBorder);

		float icon = 14f * scale;
		float iconMargin = 3f * scale;
		float labelWidth = Math.Max(Math.Min(font.Measure(label), width - icon - (iconMargin * 2f)), 0f);
		float cursor = left + Math.Max((width - icon - (iconMargin * 2f) - labelWidth) / 2f, 0f);

		painter.PushClip(left, top, width, height);
		painter.DrawIcon(Codicon.Search, cursor + iconMargin + (icon / 2f), centreY, icon, Theme.CommandCentreForeground.WithAlpha(0.8f));
		painter.PopClip();
		cursor += icon + (iconMargin * 2f);

		painter.PushClip(cursor, top, labelWidth, height);
		painter.DrawText(font, label, cursor, Baseline(font, centreY), Theme.CommandCentreForeground);
		painter.PopClip();

		AddRegion(TitleBarPart.CommandCentre, left, left + width);
	}

	private void DrawCaptionButtons(IPainter painter, float width, float height, float scale) {
		float button = CaptionButtonWidth * scale;
		float centreY = height / 2f;

		DrawCaptionButton(painter, TitleBarPart.Minimize, width - (button * 3f), button, height, centreY, scale);
		DrawCaptionButton(painter, TitleBarPart.Maximize, width - (button * 2f), button, height, centreY, scale);
		DrawCaptionButton(painter, TitleBarPart.Close, width - button, button, height, centreY, scale);
	}

	private void DrawCaptionButton(IPainter painter, TitleBarPart which, float left, float width, float height, float centreY, float scale) {
		bool hovered = Hover == which;
		bool closing = hovered && which == TitleBarPart.Close;

		if (hovered) painter.FillRectangle(left, 0f, width, height, which == TitleBarPart.Close ? Color.Parse("#e81123e6") : Color.Parse("#ffffff1a"));

		Color color = closing ? Color.Parse("#ffffff") : Theme.TitleBarForeground;

		float centreX = left + (width / 2f);
		float size = 10f * scale;
		float half = size / 2f;

		switch (which) {
			case TitleBarPart.Minimize:
				painter.DrawHorizontalLine(centreY, centreX - half, centreX + half, color);
				break;

			case TitleBarPart.Maximize when IsMaximized:
				StrokeRectangle(painter, centreX - half, centreY - half + (2f * scale), size - (2f * scale), size - (2f * scale), color);
				painter.DrawHorizontalLine(centreY - half, centreX - half + (2f * scale), centreX + half, color);
				painter.DrawVerticalLine(centreX + half, centreY - half, centreY + half - (2f * scale), color);
				break;

			case TitleBarPart.Maximize:
				StrokeRectangle(painter, centreX - half, centreY - half, size, size, color);
				break;

			case TitleBarPart.Close:
				painter.StrokeLine(centreX - half, centreY - half, centreX + half, centreY + half, 1.2f, color);
				painter.StrokeLine(centreX + half, centreY - half, centreX - half, centreY + half, 1.2f, color);
				break;
		}
	}

	private static void StrokeRectangle(IPainter painter, float x, float y, float width, float height, Color color) {
		painter.DrawHorizontalLine(y, x, x + width, color);
		painter.DrawHorizontalLine(y + height, x, x + width, color);
		painter.DrawVerticalLine(x, y, y + height, color);
		painter.DrawVerticalLine(x + width, y, y + height + 1f, color);
	}

	private static void FillWidget(IPainter painter, float left, float width, float centreY, float scale, Color color) {
		float height = WidgetHeight * scale;
		painter.FillRoundedRectangle(left, centreY - (height / 2f), width, height, Metrics.RadiusMedium * scale, color);
	}

	private static float Baseline(ITextFont font, float centreY) => centreY - (font.GetLineHeight() / 2f) + font.GetBaseline();

	private void AddRegion(TitleBarPart part, float left, float right) {
		if (_regionCount < MaxRegions) _regions[_regionCount++] = new Region(part, left, right);
	}

	private void EnsureProjectColour(string name) {
		if (_colouredName == name) return;

		_colouredName = name;
		_projectColor = NameColour(name);
		_initials = Initials(name);
	}

	private static string Initials(string name) {
		List<string> words = [];
		int start = 0;

		for (int i = 0; i <= name.Length; i++) {
			bool boundary = i == name.Length || char.IsWhiteSpace(name[i]) || name[i] is '.' or '_' or '-';
			bool camel = i > 0 && i < name.Length && char.IsLower(name[i - 1]) && char.IsUpper(name[i]);

			if (boundary || camel) {
				if (i > start) words.Add(name[start..i]);

				start = boundary ? i + 1 : i;
			}
		}

		string letters = words.Count > 1 ? $"{words[0][0]}{words[1][0]}" : name[..Math.Min(2, name.Length)];
		return letters.ToUpperInvariant();
	}

	private static Color NameColour(string name) {
		int hash = 0;

		foreach (char character in name) hash = unchecked((hash * 31) + character);

		return FromHsl(((hash % 360) + 360) % 360, 0.6f, 0.55f);
	}

	private static Color FromHsl(float hue, float saturation, float lightness) {
		float a = saturation * Math.Min(lightness, 1f - lightness);

		float Channel(float n) {
			float k = (n + (hue / 30f)) % 12f;
			return lightness - (a * Math.Max(-1f, Math.Min(Math.Min(k - 3f, 9f - k), 1f)));
		}

		return new Color(Channel(0f), Channel(8f), Channel(4f));
	}
}
