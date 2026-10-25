using Forge.Common.Render;
using Forge.Common.UI;
using Forge.Core.Native;
using Forge.Theming;
using Forge.UI.Controls;

namespace Forge.Workbench;

public sealed class TitleBar : Panel
{
	private const float WidgetHeight = 26f;
	private const float CommandCentreHeight = 22f;
	private const float CommandCentreMaxWidth = 600f;
	private const float CommandCentreWidthFraction = 0.38f;
	private const float SideFraction = 0.2f;
	private const float CommandCentreInset = 16f;
	private const float AppIconSize = 20f;
	private const float RunButtonWidth = 28f;
	private const float SeparatorWidth = 13f;
	private const float RunGap = 2f;

	private const string SignInLabel = "$(account) Sign in";

	private readonly ImagePanel _icon;
	private readonly Button _menu;
	private readonly ProjectButton _project;
	private readonly Button _branch;
	private readonly Button _account;
	private readonly CommandCentreButton _commandCentre;
	private readonly RunConfigurationButton _runConfiguration;
	private readonly Separator _runSeparator;
	private readonly Separator _stepSeparator;
	private readonly ActionButton _run;
	private readonly ActionButton _debug;
	private readonly ActionButton _continue;
	private readonly ActionButton _pause;
	private readonly ActionButton _stop;
	private readonly ActionButton _restart;
	private readonly ActionButton[] _steps;
	private readonly CaptionButton _maximize;
	private readonly CaptionButton _minimize;

	public TitleBar() {
		Name = "TitleBar";
		Dock = DockType.Top;
		MouseInputEnabled = false;

		_icon = Add(new ImagePanel { Name = "AppIcon", Dock = DockType.Left, ImageSize = AppIconSize });
		_menu = Add(new Button { Name = "Menu", Command = "forge.titleBar.openMenu", Dock = DockType.Left, Foreground = Theme.TitleBarForeground, HoverBackground = Theme.MenubarSelectionBackground, Radius = Metrics.RadiusMedium, Padding = new Thickness(Metrics.Space8, 0f, Metrics.Space8, 0f), SizeToContents = true });
		_project = Add(new ProjectButton { Command = "forge.titleBar.openProjectMenu", Dock = DockType.Left, Visible = false });
		_branch = Add(new Button { Name = "Branch", Command = "git.checkout", Dock = DockType.Left, Visible = false, Foreground = Theme.TitleBarForeground, Radius = Metrics.RadiusMedium, Padding = new Thickness(Metrics.Space6, 0f, Metrics.Space6, 0f), IconColor = icon => Theme.TitleBarForeground.WithAlpha(icon == Codicon.GitBranch ? 0.8f : 0.55f), SizeToContents = true });
		_account = Add(new Button { Name = "Account", Dock = DockType.Left, Foreground = Theme.TitleBarForeground, Radius = Metrics.RadiusMedium, Padding = new Thickness(Metrics.Space4, 0f, Metrics.Space8, 0f), SizeToContents = true });

		Add(new CaptionButton(CaptionKind.Close));
		_maximize = Add(new CaptionButton(CaptionKind.Maximize));
		_minimize = Add(new CaptionButton(CaptionKind.Minimize));

		_steps = [
			RunAction("workbench.action.debug.stepOut", Codicon.DebugStepOut, Theme.DebugIconStep),
			RunAction("workbench.action.debug.stepOver", Codicon.DebugStepOver, Theme.DebugIconStep),
			RunAction("workbench.action.debug.stepInto", Codicon.DebugStepInto, Theme.DebugIconStep),
			RunAction("workbench.action.debug.callStackTop", Codicon.DebugStackframe, Theme.DebugIconPause),
		];

		_stepSeparator = Add(new Separator { Name = "StepSeparator", Dock = DockType.Right });
		_restart = RunAction("workbench.action.debug.restart", Codicon.DebugRestart, Theme.DebugIconRestart);
		_stop = RunAction("workbench.action.debug.stop", Codicon.DebugStop, Theme.DebugIconStop);
		_pause = RunAction("workbench.action.debug.pause", Codicon.DebugPause, Theme.DebugIconPause);
		_continue = RunAction("workbench.action.debug.continue", Codicon.DebugContinue, Theme.DebugIconStart);
		_debug = RunAction("workbench.action.debug.start", Codicon.DebugAlt, Theme.DebugIconStart);
		_run = RunAction("workbench.action.debug.run", Codicon.DebugStart, Theme.DebugIconStart);
		_runSeparator = Add(new Separator { Name = "RunSeparator", Dock = DockType.Right });
		_runConfiguration = Add(new RunConfigurationButton { Dock = DockType.Right });

		_commandCentre = Add(new CommandCentreButton());

		_menu.SetText("Forge");
		_account.SetText(SignInLabel);
		SyncRunCluster();
	}

	public static float Height(float scale) => Metrics.TitleBarHeight * scale;

	public string Title {
		get;
		set {
			if (field == value) return;

			field = value;
			_commandCentre.SetText($"$(search) {value}");
			InvalidateLayout();
		}
	} = string.Empty;

	public string? FolderName {
		get;
		set {
			field = value;
			_project.SetProject(ProjectName);
		}
	}

	public string? BranchName {
		get;
		set {
			if (field == value) return;

			field = value;
			_branch.Visible = value is { Length: > 0 };

			if (_branch.Visible) _branch.SetText($"$(git-branch) {value} $(chevron-down)");
		}
	}

	public string? RemoteOwner { get; set; }

	public string? RemoteRepository {
		get;
		set {
			field = value;
			_project.SetProject(ProjectName);
		}
	}

	public string? AccountName {
		get;
		set {
			if (field == value) return;

			field = value;
			_account.SetText(value is { Length: > 0 } name ? $"$(account) {name}" : SignInLabel);
		}
	}

	public IReadOnlyList<string> DebugConfigurations {
		get;
		set {
			field = value;
			SyncRunCluster();
		}
	} = [];

	public string? SelectedDebugConfiguration {
		get;
		set {
			if (field == value) return;

			field = value;
			SyncRunCluster();
		}
	}

	public bool IsDebugging {
		get;
		set {
			if (field == value) return;

			field = value;
			SyncRunCluster();
		}
	}

	public bool IsPaused {
		get;
		set {
			if (field == value) return;

			field = value;
			SyncRunCluster();
		}
	}

	public bool IsMaximized {
		get => _maximize.Maximized;
		set => _maximize.Maximized = value;
	}

	public bool MenuOpen {
		get => _menu.Active;
		set => _menu.Active = value;
	}

	public IImage? Icon {
		get => _icon.Image;
		set => _icon.Image = value;
	}

	public float MenuLeft => _menu.ScreenX;

	public float ProjectLeft => _project.ScreenX;

	public float RunConfigurationLeft => _runConfiguration.ScreenX;

	public string? ProjectName => RemoteRepository is { Length: > 0 } repository ? repository : FolderName;

	public nint HitTest(float x, float y) {
		if (y < 0f || y >= Tall) return Win32.HtClient;

		return IsWithinTraverse(x, y) switch {
			CaptionButton caption => caption.HitCode,
			null => Win32.HtCaption,
			_ => Win32.HtClient,
		};
	}

	private T Add<T>(T child) where T : Panel {
		AddChild(child);
		return child;
	}

	private ActionButton RunAction(string command, Codicon icon, Color color) => Add(new ActionButton {
		Name = command,
		Command = command,
		Icon = icon,
		Dock = DockType.Right,
		Foreground = color,
		Radius = Metrics.RadiusMedium,
	});

	private void SyncRunCluster() {
		bool shown = DebugConfigurations.Count > 0 || IsDebugging;
		bool debugging = shown && IsDebugging;

		_runConfiguration.Visible = shown;
		_runSeparator.Visible = shown;
		_run.Visible = shown && !debugging;
		_debug.Visible = shown && !debugging;
		_continue.Visible = debugging && IsPaused;
		_pause.Visible = debugging && !IsPaused;
		_stop.Visible = debugging;
		_restart.Visible = debugging;
		_stepSeparator.Visible = debugging;

		foreach (ActionButton step in _steps) {
			step.Visible = debugging;
			step.Enabled = IsPaused;
		}

		_runConfiguration.Debugging = IsDebugging;
		_runConfiguration.Paused = IsPaused;
		_runConfiguration.SetText($"{SelectedDebugConfiguration ?? (DebugConfigurations.Count > 0 ? DebugConfigurations[0] : "Add Configuration...")} $(chevron-down)");
	}

	public override void PerformLayout() {
		float scale = Scale;
		float height = Height(scale);
		float vertical = (Metrics.TitleBarHeight - WidgetHeight) / 2f * scale;
		float runButton = RunButtonWidth * scale;
		Thickness widget = new(0f, vertical, 0f, vertical);

		_icon.SetSize(Metrics.ActivityBarWidth * scale, height);
		_icon.DockMargin = new Thickness(Metrics.CardMargin * scale, 0f, 0f, 0f);
		_menu.DockMargin = widget with { Left = Metrics.Space4 * scale };
		_project.DockMargin = widget with { Left = Metrics.Space16 * scale };
		_branch.DockMargin = widget with { Left = Metrics.Space2 * scale };
		_account.DockMargin = widget with { Left = Metrics.Space2 * scale };

		foreach (Panel child in Children) {
			switch (child) {
				case CaptionButton caption:
					caption.SetSize(CaptionButton.WidthFor(scale), height);
					break;

				case ActionButton action:
					action.SetSize(runButton, action.Tall);
					action.DockMargin = widget with { Left = RunGap * scale };
					break;

				case Separator separator:
					separator.SetSize(SeparatorWidth * scale, height);
					break;

				case Label label when label.SizeToContents:
					label.FitContents();
					break;
			}
		}

		_runConfiguration.DockMargin = widget with { Right = RunGap * scale };
		_minimize.DockMargin = new Thickness(Metrics.Space8 * scale, 0f, 0f, 0f);

		base.PerformLayout();

		PlaceCommandCentre(scale);
	}

	private void PlaceCommandCentre(float scale) {
		float leftEnd = 0f;
		float rightStart = Wide;

		foreach (Panel child in Children) {
			if (!child.Visible) continue;

			if (child.Dock == DockType.Left) leftEnd = MathF.Max(leftEnd, child.X + child.Wide);
			else if (child.Dock == DockType.Right) rightStart = MathF.Min(rightStart, child.X - (Metrics.Space4 * scale));
		}

		float leftBox = MathF.Max(Wide * SideFraction, leftEnd);
		float rightBox = MathF.Max(Wide * SideFraction, Wide - rightStart);
		float available = MathF.Max(Wide - leftBox - rightBox, 0f);
		float inset = CommandCentreInset * scale;
		float preferred = MathF.Min(_commandCentre.PreferredWidth, MathF.Min(Wide * CommandCentreWidthFraction, CommandCentreMaxWidth * scale)) + (inset * 2f);
		float outer = MathF.Min(preferred, available);
		float width = outer - (inset * 2f);
		float height = CommandCentreHeight * scale;

		_commandCentre.Visible = width > 0f;
		_commandCentre.SetBounds(leftBox + ((available - outer) / 2f) + inset, (Tall - height) / 2f, MathF.Max(width, 0f), height);
	}

	public override void PaintBackground(IPainter painter) => painter.FillRectangle(ScreenX, ScreenY, Wide, Tall, Theme.TitleBarBackground);
}
