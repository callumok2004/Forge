namespace Forge.Theming;

public static class Theme
{
	public static readonly Color EditorBackground = Color.Parse("#111216");
	public static readonly Color EditorForeground = Color.Parse("#c9ccd6");
	public static readonly Color CurrentLineBackground = Color.Parse("#191c23");
	public static readonly Color SelectionBackground = Color.Parse("#7c5cff40");
	public static readonly Color CursorForeground = Color.Parse("#7c5cff");
	public static readonly Color IndentGuide = Color.Parse("#24272f");
	public static readonly Color IndentGuideActive = Color.Parse("#3a3e4c");
	public static readonly Color RulerForeground = Color.Parse("#24272f");

	public static readonly Color LineNumberForeground = Color.Parse("#464b58");
	public static readonly Color LineNumberActiveForeground = Color.Parse("#9a8cff");

	public static readonly Color ActivityBarBackground = Color.Parse("#0b0c0e");
	public static readonly Color ActivityBarForeground = Color.Parse("#5a5e6b");
	public static readonly Color ActivityBarActiveForeground = Color.Parse("#c9ccd6");
	public static readonly Color ActivityBarActiveBackground = Color.Parse("#1e2129");
	public static readonly Color ActivityBarActiveBorder = Color.Parse("#7c5cff");

	public static readonly Color BreadcrumbBackground = Color.Parse("#0e0f12");
	public static readonly Color BreadcrumbForeground = Color.Parse("#7a7e8b");
	public static readonly Color BreadcrumbActiveForeground = Color.Parse("#c9ccd6");
	public static readonly Color MinimapSlider = Color.Parse("#ffffff14");

	public static readonly Color ChromeBackground = Color.Parse("#0b0c0e");
	public static readonly Color ChromeForeground = Color.Parse("#9a9daa");
	public static readonly Color ChromeBorder = Color.Parse("#1c1f27");
	public static readonly Color PanelBorder = Color.Parse("#17181d");
	public static readonly Color TitleBarBackground = Color.Parse("#0b0c0e");
	public static readonly Color TitleBarForeground = Color.Parse("#c9ccd6");

	public static readonly Color CommandCentreBackground = Color.Parse("#16181e");
	public static readonly Color CommandCentreHoverBackground = Color.Parse("#1e2129");

	public static readonly Color CommandCentreBorder = Color.Parse("#c9ccd6").WithAlpha(0.2f);
	public static readonly Color StatusBarForeground = Color.Parse("#8a8e9b");
	public static readonly Color StatusBarHoverBackground = Color.Parse("#ffffff12");
	public static readonly Color SectionHeaderForeground = Color.Parse("#7a7e8b");
	public static readonly Color SidebarTitleForeground = Color.Parse("#8a8e9b");
	public static readonly Color SidebarFileForeground = Color.Parse("#9a9daa");

	public static readonly Color TabActiveBackground = Color.Parse("#0e0f12");
	public static readonly Color TabActiveForeground = Color.Parse("#fafafa");
	public static readonly Color TabInactiveForeground = Color.Parse("#6a6e7b");
	public static readonly Color TabHoverBackground = Color.Parse("#1e2129");
	public static readonly Color TabActiveBorderTop = Color.Parse("#7c5cff");

	public static readonly Color ListHoverBackground = Color.Parse("#191c23");
	public static readonly Color ListActiveSelectionBackground = Color.Parse("#7c5cff33");
	public static readonly Color WidgetBackground = Color.Parse("#1c1f27");
	public static readonly Color WidgetBorder = Color.Parse("#23252d");

	public static readonly Color SuggestForeground = Color.Parse("#9a9a9a");

	public static readonly Color SuggestSelectedForeground = Color.Parse("#ffffff");

	public static readonly Color SuggestHighlightForeground = Color.Parse("#2aaaff");

	public static readonly Color HoverOverlay = Color.Parse("#ffffff14");

	public static readonly Color PressedOverlay = Color.Parse("#00000033");

	public static readonly Color Accent = Color.Parse("#7c5cff");
	public static readonly Color AccentMuted = Color.Parse("#9a8cff");
	public static readonly Color ScrollbarSlider = Color.Parse("#ffffff14");
	public static readonly Color ScrollbarSliderHover = Color.Parse("#ffffff22");

	public static readonly Color ErrorForeground = Color.Parse("#ff7a84");
	public static readonly Color WarningForeground = Color.Parse("#cd9731");
	public static readonly Color SuccessForeground = Color.Parse("#6bd68a");

	public static readonly Color GitModifiedForeground = Color.Parse("#c9a2ff");
	public static readonly Color GitUntrackedForeground = Color.Parse("#6bd68a");
	public static readonly Color GitIgnoredForeground = Color.Parse("#444444");

	public static readonly Color GitAddedForeground = Color.Parse("#81b88b");

	public static readonly Color GitDeletedForeground = Color.Parse("#c74e39");

	public static readonly Color DebugBreakpointForeground = Color.Parse("#e51400");
	public static readonly Color DebugBreakpointDisabled = Color.Parse("#848484");
	public static readonly Color DebugBreakpointConditional = Color.Parse("#e5a000");
	public static readonly Color DebugBreakpointLogPoint = Color.Parse("#5aa6e5");
	public static readonly Color DebugBreakpointHover = Color.Parse("#e5140055");
	public static readonly Color DebugStackframeForeground = Color.Parse("#ffcc00");
	public static readonly Color DebugExecutionBackground = Color.Parse("#ffff0033");
	public static readonly Color DebugFocusedFrameBackground = Color.Parse("#7abd7a26");
	public static readonly Color DebugToolBarBackground = Color.Parse("#333333");
	public static readonly Color DebugStatusBarBackground = Color.Parse("#cc6633");

	public static readonly Color GitRenamedForeground = Color.Parse("#73c991");

	public static readonly Color GitConflictForeground = Color.Parse("#e4676b");
}
