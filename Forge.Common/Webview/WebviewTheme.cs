using System.Text;

using Forge.Theming;

namespace Forge.Common.Webview;

public static class WebviewTheme
{
	public const string Kind = "vscode-dark";

	public const string Name = "Forge Dark";

	public static string BuildCss(string codeFontFamily, float codeFontSize) {
		StringBuilder builder = new(4096);

		builder.Append(":root{");

		Append(builder, "font-family", "\"Segoe WPC\", \"Segoe UI\", sans-serif");
		Append(builder, "font-size", "13px");
		Append(builder, "font-weight", "normal");
		Append(builder, "editor-font-family",
			$"\"{codeFontFamily}\", Consolas, \"Courier New\", monospace");
		Append(builder, "editor-font-size",
			$"{codeFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px");
		Append(builder, "editor-font-weight", "normal");

		builder.Append("--monaco-monospace-font:var(--vscode-editor-font-family);")
			.Append("--text-link-decoration:underline;");

		Append(builder, "foreground", Theme.EditorForeground);
		Append(builder, "disabledForeground", Theme.SectionHeaderForeground);
		Append(builder, "descriptionForeground", Theme.SectionHeaderForeground);
		Append(builder, "errorForeground", Theme.ErrorForeground);
		Append(builder, "focusBorder", Theme.Accent);
		Append(builder, "selection-background", Theme.SelectionBackground);
		Append(builder, "icon-foreground", Theme.ChromeForeground);
		Append(builder, "widget-border", Theme.WidgetBorder);
		Append(builder, "widget-shadow", Color.Parse("#00000066"));
		Append(builder, "sash-hoverBorder", Theme.Accent);

		Append(builder, "editor-background", Theme.ChromeBackground);
		Append(builder, "sideBar-dropBackground", Css(Theme.ListHoverBackground));
		Append(builder, "editor-foreground", Theme.EditorForeground);
		Append(builder, "editor-selectionBackground", Theme.SelectionBackground);
		Append(builder, "editorWidget-background", Theme.WidgetBackground);
		Append(builder, "editorWidget-foreground", Theme.EditorForeground);
		Append(builder, "editorWidget-border", Theme.WidgetBorder);
		Append(builder, "editorGroup-border", Theme.PanelBorder);
		Append(builder, "editorError-foreground", Theme.ErrorForeground);
		Append(builder, "editorWarning-foreground", Theme.WarningForeground);

		Append(builder, "button-background", Theme.Accent);
		Append(builder, "button-foreground", Color.Parse("#ffffff"));
		Append(builder, "button-hoverBackground", Theme.AccentMuted);
		Append(builder, "button-border", Theme.WidgetBorder);
		Append(builder, "button-secondaryBackground", Theme.WidgetBackground);
		Append(builder, "button-secondaryForeground", Theme.EditorForeground);
		Append(builder, "button-secondaryHoverBackground", Theme.ListHoverBackground);

		Append(builder, "input-background", Theme.WidgetBackground);
		Append(builder, "input-foreground", Theme.EditorForeground);
		Append(builder, "input-border", Theme.WidgetBorder);
		Append(builder, "input-placeholderForeground", Theme.SectionHeaderForeground);
		Append(builder, "inputOption-activeBorder", Theme.Accent);
		Append(builder, "inputValidation-errorBackground", Theme.WidgetBackground);
		Append(builder, "inputValidation-errorBorder", Theme.ErrorForeground);

		Append(builder, "dropdown-background", Theme.WidgetBackground);
		Append(builder, "dropdown-foreground", Theme.EditorForeground);
		Append(builder, "dropdown-border", Theme.WidgetBorder);
		Append(builder, "dropdown-listBackground", Theme.WidgetBackground);

		Append(builder, "checkbox-background", Theme.WidgetBackground);
		Append(builder, "checkbox-foreground", Theme.EditorForeground);
		Append(builder, "checkbox-border", Theme.WidgetBorder);

		Append(builder, "list-hoverBackground", Theme.ListHoverBackground);
		Append(builder, "list-hoverForeground", Theme.EditorForeground);
		Append(builder, "list-activeSelectionBackground", Theme.ListActiveSelectionBackground);
		Append(builder, "list-activeSelectionForeground", Theme.TabActiveForeground);
		Append(builder, "list-inactiveSelectionBackground", Theme.ListHoverBackground);
		Append(builder, "list-highlightForeground", Theme.SuggestHighlightForeground);

		Append(builder, "sideBar-background", Theme.ChromeBackground);
		Append(builder, "sideBar-foreground", Theme.ChromeForeground);
		Append(builder, "sideBar-border", Theme.PanelBorder);
		Append(builder, "sideBarTitle-foreground", Theme.SidebarTitleForeground);
		Append(builder, "sideBarSectionHeader-background", Theme.ChromeBackground);
		Append(builder, "sideBarSectionHeader-foreground", Theme.SectionHeaderForeground);
		Append(builder, "sideBarSectionHeader-border", Theme.PanelBorder);

		Append(builder, "panel-background", Theme.ChromeBackground);
		Append(builder, "panel-border", Theme.PanelBorder);
		Append(builder, "panelTitle-activeForeground", Theme.TabActiveForeground);
		Append(builder, "panelTitle-inactiveForeground", Theme.TabInactiveForeground);

		Append(builder, "titleBar-activeBackground", Theme.TitleBarBackground);
		Append(builder, "titleBar-activeForeground", Theme.TitleBarForeground);

		Append(builder, "statusBar-background", Theme.ChromeBackground);
		Append(builder, "statusBar-foreground", Theme.StatusBarForeground);

		Append(builder, "textLink-foreground", Theme.AccentMuted);
		Append(builder, "textLink-activeForeground", Theme.Accent);
		Append(builder, "textPreformat-foreground", Theme.SuggestHighlightForeground);
		Append(builder, "textPreformat-background", Theme.WidgetBackground);
		Append(builder, "textCodeBlock-background", Theme.WidgetBackground);
		Append(builder, "textBlockQuote-background", Theme.WidgetBackground);
		Append(builder, "textBlockQuote-border", Theme.Accent);
		Append(builder, "textSeparator-foreground", Theme.PanelBorder);

		Append(builder, "scrollbar-shadow", Color.Parse("#00000000"));
		Append(builder, "scrollbarSlider-background", Theme.ScrollbarSlider);
		Append(builder, "scrollbarSlider-hoverBackground", Theme.ScrollbarSliderHover);
		Append(builder, "scrollbarSlider-activeBackground", Theme.ScrollbarSliderHover);

		Append(builder, "badge-background", Theme.Accent);
		Append(builder, "badge-foreground", Color.Parse("#ffffff"));
		Append(builder, "progressBar-background", Theme.Accent);

		Append(builder, "toolbar-hoverBackground", Theme.ListHoverBackground);
		Append(builder, "toolbar-activeBackground", Theme.TabHoverBackground);

		Append(builder, "menu-background", Theme.WidgetBackground);
		Append(builder, "menu-foreground", Theme.EditorForeground);
		Append(builder, "menu-border", Theme.WidgetBorder);
		Append(builder, "menu-selectionBackground", Theme.ListHoverBackground);

		Append(builder, "quickInput-background", Theme.WidgetBackground);
		Append(builder, "quickInput-foreground", Theme.EditorForeground);

		Append(builder, "charts-red", Theme.ErrorForeground);
		Append(builder, "charts-green", Theme.SuccessForeground);
		Append(builder, "charts-yellow", Theme.WarningForeground);
		Append(builder, "charts-blue", Theme.SuggestHighlightForeground);
		Append(builder, "charts-purple", Theme.Accent);
		Append(builder, "charts-foreground", Theme.EditorForeground);
		Append(builder, "charts-lines", Theme.PanelBorder);

		Append(builder, "keybindingLabel-background", Theme.WidgetBackground);
		Append(builder, "keybindingLabel-foreground", Theme.EditorForeground);
		Append(builder, "keybindingLabel-border", Theme.WidgetBorder);
		Append(builder, "keybindingLabel-bottomBorder", Theme.WidgetBorder);

		Append(builder, "editorHoverWidget-background", Theme.WidgetBackground);
		Append(builder, "editorHoverWidget-foreground", Theme.EditorForeground);
		Append(builder, "editorHoverWidget-border", Theme.WidgetBorder);

		Append(builder, "notifications-background", Theme.WidgetBackground);
		Append(builder, "notifications-foreground", Theme.EditorForeground);
		Append(builder, "notifications-border", Theme.WidgetBorder);
		Append(builder, "notificationsErrorIcon-foreground", Theme.ErrorForeground);
		Append(builder, "notificationsWarningIcon-foreground", Theme.WarningForeground);
		Append(builder, "notificationsInfoIcon-foreground", Theme.SuggestHighlightForeground);

		Append(builder, "diffEditor-insertedTextBackground", Theme.GitAddedForeground.WithAlpha(0.18f));
		Append(builder, "diffEditor-removedTextBackground", Theme.GitDeletedForeground.WithAlpha(0.18f));
		Append(builder, "diffEditor-insertedLineBackground", Theme.GitAddedForeground.WithAlpha(0.10f));
		Append(builder, "diffEditor-removedLineBackground", Theme.GitDeletedForeground.WithAlpha(0.10f));
		Append(builder, "diffEditor-border", Theme.PanelBorder);

		Append(builder, "terminal-background", Theme.EditorBackground);
		Append(builder, "terminal-foreground", Theme.EditorForeground);
		Append(builder, "terminal-border", Theme.PanelBorder);
		Append(builder, "terminal-selectionBackground", Theme.SelectionBackground);
		Append(builder, "terminal-ansiBlack", Color.Parse("#0b0c0e"));
		Append(builder, "terminal-ansiRed", Theme.ErrorForeground);
		Append(builder, "terminal-ansiGreen", Theme.SuccessForeground);
		Append(builder, "terminal-ansiYellow", Theme.WarningForeground);
		Append(builder, "terminal-ansiBlue", Theme.SuggestHighlightForeground);
		Append(builder, "terminal-ansiMagenta", Theme.Accent);
		Append(builder, "terminal-ansiCyan", Color.Parse("#4ec9b0"));
		Append(builder, "terminal-ansiWhite", Theme.EditorForeground);
		Append(builder, "terminal-ansiBrightBlack", Theme.SectionHeaderForeground);
		Append(builder, "terminal-ansiBrightRed", Theme.ErrorForeground);
		Append(builder, "terminal-ansiBrightGreen", Theme.SuccessForeground);
		Append(builder, "terminal-ansiBrightYellow", Theme.WarningForeground);
		Append(builder, "terminal-ansiBrightBlue", Theme.SuggestHighlightForeground);
		Append(builder, "terminal-ansiBrightMagenta", Theme.AccentMuted);
		Append(builder, "terminal-ansiBrightCyan", Color.Parse("#4ec9b0"));
		Append(builder, "terminal-ansiBrightWhite", Theme.TabActiveForeground);

		Append(builder, "tab-activeBackground", Theme.TabActiveBackground);
		Append(builder, "tab-activeForeground", Theme.TabActiveForeground);
		Append(builder, "tab-inactiveForeground", Theme.TabInactiveForeground);
		Append(builder, "tab-border", Theme.PanelBorder);

		Append(builder, "gitDecoration-addedResourceForeground", Theme.GitAddedForeground);
		Append(builder, "gitDecoration-modifiedResourceForeground", Theme.GitModifiedForeground);
		Append(builder, "gitDecoration-deletedResourceForeground", Theme.GitDeletedForeground);
		Append(builder, "gitDecoration-untrackedResourceForeground", Theme.GitUntrackedForeground);

		Append(builder, "actionBar-toggledBackground", Theme.ListActiveSelectionBackground);
		Append(builder, "banner-background", Theme.WidgetBackground);
		Append(builder, "banner-foreground", Theme.EditorForeground);
		Append(builder, "banner-iconForeground", Theme.EditorForeground);
		Append(builder, "button-separator", Theme.PanelBorder);
		Append(builder, "charts-orange", Theme.EditorForeground);
		Append(builder, "chat-font-family", "var(--vscode-font-family)");
		Append(builder, "chat-font-size", "var(--vscode-font-size)");
		Append(builder, "chat-slashCommandBackground", Theme.WidgetBackground);
		Append(builder, "chat-slashCommandForeground", Theme.EditorForeground);
		Append(builder, "contrastActiveBorder", "transparent");
		Append(builder, "contrastBorder", "transparent");
		Append(builder, "diffEditor-diagonalFill", Theme.PanelBorder);
		Append(builder, "diffEditor-insertedTextBorder", Theme.GitAddedForeground);
		Append(builder, "diffEditor-move-border", Theme.WidgetBorder);
		Append(builder, "diffEditor-moveActive-border", Theme.WidgetBorder);
		Append(builder, "diffEditor-removedTextBorder", Theme.GitDeletedForeground);
		Append(builder, "diffEditor-unchangedCodeBackground", Theme.WidgetBackground);
		Append(builder, "diffEditor-unchangedRegionBackground", Theme.WidgetBackground);
		Append(builder, "diffEditor-unchangedRegionForeground", Theme.SectionHeaderForeground);
		Append(builder, "diffEditor-unchangedRegionShadow", Color.Parse("#00000066"));
		Append(builder, "diffEditorGutter-insertedLineBackground", Theme.GitAddedForeground.WithAlpha(0.18f));
		Append(builder, "diffEditorGutter-removedLineBackground", Theme.GitDeletedForeground.WithAlpha(0.18f));
		Append(builder, "editor-findMatchBackground", Theme.Accent.WithAlpha(0.35f));
		Append(builder, "editor-findMatchBorder", Theme.WidgetBorder);
		Append(builder, "editor-findMatchHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-findRangeHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-foldBackground", Theme.SelectionBackground);
		Append(builder, "editor-foldPlaceholderForeground", Theme.SectionHeaderForeground);
		Append(builder, "editor-hoverHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-inactiveSelectionBackground", Theme.ListActiveSelectionBackground);
		Append(builder, "editor-lineHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-linkedEditingBackground", Theme.AccentMuted);
		Append(builder, "editor-placeholder-foreground", Theme.SectionHeaderForeground);
		Append(builder, "editor-rangeHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-rangeHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-selectionHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-selectionHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-snippetFinalTabstopHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-snippetFinalTabstopHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-snippetTabstopHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-snippetTabstopHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-symbolHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-symbolHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-wordHighlightBackground", Theme.SelectionBackground);
		Append(builder, "editor-wordHighlightBorder", Theme.WidgetBorder);
		Append(builder, "editor-wordHighlightStrongBackground", Theme.WidgetBackground);
		Append(builder, "editor-wordHighlightStrongBorder", Theme.WidgetBorder);
		Append(builder, "editor-wordHighlightTextBackground", Theme.WidgetBackground);
		Append(builder, "editor-wordHighlightTextBorder", Theme.WidgetBorder);
		Append(builder, "editorActionList-background", Theme.WidgetBackground);
		Append(builder, "editorActionList-focusBackground", Theme.ListHoverBackground);
		Append(builder, "editorActionList-focusForeground", Theme.TabActiveForeground);
		Append(builder, "editorActionList-foreground", Theme.EditorForeground);
		Append(builder, "editorBracketMatch-background", Theme.SelectionBackground);
		Append(builder, "editorBracketMatch-border", Theme.WidgetBorder);
		Append(builder, "editorCodeLens-fontFamily", "var(--vscode-editor-font-family)");
		Append(builder, "editorCodeLens-fontFamilyDefault", "var(--vscode-editor-font-family)");
		Append(builder, "editorCodeLens-fontFeatureSettings", "normal");
		Append(builder, "editorCodeLens-fontSize", "0.9em");
		Append(builder, "editorCodeLens-foreground", Theme.EditorForeground);
		Append(builder, "editorCodeLens-lineHeight", "1.4");
		Append(builder, "editorCursor-foreground", Theme.CursorForeground);
		Append(builder, "editorError-background", Theme.ErrorForeground);
		Append(builder, "editorError-border", Theme.ErrorForeground);
		Append(builder, "editorGhostText-background", Theme.WidgetBackground);
		Append(builder, "editorGhostText-border", Theme.WidgetBorder);
		Append(builder, "editorGhostText-foreground", Theme.SectionHeaderForeground);
		Append(builder, "editorGutter-background", Theme.EditorBackground);
		Append(builder, "editorGutter-commentRangeForeground", Theme.EditorForeground);
		Append(builder, "editorGutter-foldingControlForeground", Theme.EditorForeground);
		Append(builder, "editorHint-border", Theme.WidgetBorder);
		Append(builder, "editorHoverWidget-highlightForeground", Theme.SuggestHighlightForeground);
		Append(builder, "editorHoverWidget-statusBarBackground", Theme.WidgetBackground);
		Append(builder, "editorInfo-background", Theme.SuggestHighlightForeground);
		Append(builder, "editorInfo-border", Theme.SuggestHighlightForeground);
		Append(builder, "editorLightBulb-foreground", Theme.WarningForeground);
		Append(builder, "editorLightBulbAi-foreground", Theme.WarningForeground);
		Append(builder, "editorLightBulbAutoFix-foreground", Theme.WarningForeground);
		Append(builder, "editorLineNumber-activeForeground", Theme.LineNumberActiveForeground);
		Append(builder, "editorLineNumber-foreground", Theme.LineNumberForeground);
		Append(builder, "editorLink-activeForeground", Theme.AccentMuted);
		Append(builder, "editorMarkerNavigationInfo-headerBackground", Theme.SuggestHighlightForeground);
		Append(builder, "editorRuler-foreground", Theme.EditorForeground);
		Append(builder, "editorStickyScroll-background", Theme.EditorBackground);
		Append(builder, "editorStickyScroll-border", Theme.WidgetBorder);
		Append(builder, "editorStickyScroll-foldingOpacityTransition", "0.5s linear");
		Append(builder, "editorStickyScroll-scrollableWidth", "0px");
		Append(builder, "editorStickyScroll-shadow", Color.Parse("#00000066"));
		Append(builder, "editorStickyScrollHover-background", Theme.WidgetBackground);
		Append(builder, "editorSuggestWidget-background", Theme.WidgetBackground);
		Append(builder, "editorSuggestWidget-border", Theme.WidgetBorder);
		Append(builder, "editorSuggestWidget-focusHighlightForeground", Theme.SuggestHighlightForeground);
		Append(builder, "editorSuggestWidget-foreground", Theme.EditorForeground);
		Append(builder, "editorSuggestWidget-highlightForeground", Theme.SuggestHighlightForeground);
		Append(builder, "editorSuggestWidget-selectedForeground", Theme.EditorForeground);
		Append(builder, "editorSuggestWidget-selectedIconForeground", Theme.EditorForeground);
		Append(builder, "editorSuggestWidgetStatus-foreground", Theme.EditorForeground);
		Append(builder, "editorUnicodeHighlight-background", Theme.WidgetBackground);
		Append(builder, "editorUnicodeHighlight-border", Theme.WidgetBorder);
		Append(builder, "editorUnnecessaryCode-border", Theme.WidgetBorder);
		Append(builder, "editorWarning-background", Theme.WarningForeground);
		Append(builder, "editorWarning-border", Theme.WarningForeground);
		Append(builder, "editorWhitespace-foreground", Theme.SectionHeaderForeground);
		Append(builder, "editorWidget-resizeBorder", Theme.WidgetBorder);
		Append(builder, "hover-maxWidth", "500px");
		Append(builder, "hover-sourceWhiteSpace", "pre");
		Append(builder, "hover-whiteSpace", "normal");
		Append(builder, "inlineChatInput-border", Theme.WidgetBorder);
		Append(builder, "inputOption-hoverBackground", Theme.ListHoverBackground);
		Append(builder, "inputValidation-infoBorder", Theme.SuggestHighlightForeground);
		Append(builder, "list-focusHighlightForeground", Theme.SuggestHighlightForeground);
		Append(builder, "menu-selectionBorder", Theme.WidgetBorder);
		Append(builder, "menu-selectionForeground", Theme.TabActiveForeground);
		Append(builder, "menu-separatorBackground", Theme.PanelBorder);
		Append(builder, "minimapSlider-activeBackground", Theme.ScrollbarSlider);
		Append(builder, "minimapSlider-background", Theme.ScrollbarSlider);
		Append(builder, "minimapSlider-hoverBackground", Theme.ScrollbarSlider);
		Append(builder, "multiDiffEditor-background", Theme.WidgetBackground);
		Append(builder, "multiDiffEditor-border", Theme.WidgetBorder);
		Append(builder, "multiDiffEditor-headerBackground", Theme.WidgetBackground);
		Append(builder, "parameterHintsWidget-editorFontFamily", "var(--vscode-editor-font-family)");
		Append(builder, "parameterHintsWidget-editorFontFamilyDefault", "var(--vscode-editor-font-family)");
		Append(builder, "peekViewEditor-background", Theme.EditorBackground);
		Append(builder, "peekViewEditor-matchHighlightBackground", Theme.SelectionBackground);
		Append(builder, "peekViewEditor-matchHighlightBorder", Theme.WidgetBorder);
		Append(builder, "peekViewEditorGutter-background", Theme.EditorBackground);
		Append(builder, "peekViewEditorStickyScroll-background", Theme.EditorBackground);
		Append(builder, "peekViewResult-background", Theme.WidgetBackground);
		Append(builder, "peekViewResult-fileForeground", Theme.EditorForeground);
		Append(builder, "peekViewResult-lineForeground", Theme.EditorForeground);
		Append(builder, "peekViewResult-matchHighlightBackground", Theme.SelectionBackground);
		Append(builder, "peekViewResult-selectionBackground", Theme.ListActiveSelectionBackground);
		Append(builder, "peekViewResult-selectionForeground", Theme.TabActiveForeground);
		Append(builder, "problemsErrorIcon-foreground", Theme.ErrorForeground);
		Append(builder, "problemsInfoIcon-foreground", Theme.SuggestHighlightForeground);
		Append(builder, "problemsWarningIcon-foreground", Theme.WarningForeground);
		Append(builder, "sash-hover-size", "4px");
		Append(builder, "sash-size", "4px");
		Append(builder, "sideBarActivityBarTop-border", Theme.WidgetBorder);
		Append(builder, "symbolIcon-arrayForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-booleanForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-classForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-colorForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-constantForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-constructorForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-enumeratorForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-enumeratorMemberForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-eventForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-fieldForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-fileForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-folderForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-functionForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-interfaceForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-keyForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-keywordForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-methodForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-moduleForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-namespaceForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-nullForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-numberForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-objectForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-operatorForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-packageForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-propertyForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-referenceForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-snippetForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-stringForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-structForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-textForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-typeParameterForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-unitForeground", Theme.SuggestHighlightForeground);
		Append(builder, "symbolIcon-variableForeground", Theme.SuggestHighlightForeground);

		builder.Append('}');

		builder.Append(DefaultStyles);

		return builder.ToString();
	}

	private const string DefaultStyles = "@layer vscode-default{html{scrollbar-color:var(--vscode-scrollbarSlider-background) var(--vscode-editor-background);}body{overscroll-behavior-x:none;background-color:transparent;color:var(--vscode-editor-foreground);font-family:var(--vscode-font-family);font-weight:var(--vscode-font-weight);font-size:var(--vscode-font-size);margin:0;padding:0 20px;}img,video{max-width:100%;max-height:100%;}a,a code{color:var(--vscode-textLink-foreground);}p > a{text-decoration:var(--text-link-decoration);}a:hover{color:var(--vscode-textLink-activeForeground);}a:focus,input:focus,select:focus,textarea:focus{outline:1px solid -webkit-focus-ring-color;outline-offset:-1px;}code{font-family:var(--monaco-monospace-font);color:var(--vscode-textPreformat-foreground);background-color:var(--vscode-textPreformat-background);padding:1px 3px;border-radius:4px;}pre code{padding:0;}blockquote{background:var(--vscode-textBlockQuote-background);border-color:var(--vscode-textBlockQuote-border);}kbd{background-color:var(--vscode-keybindingLabel-background);color:var(--vscode-keybindingLabel-foreground);border-style:solid;border-width:1px;border-radius:3px;border-color:var(--vscode-keybindingLabel-border);border-bottom-color:var(--vscode-keybindingLabel-bottomBorder);box-shadow:inset 0 -1px 0 var(--vscode-widget-shadow);vertical-align:middle;padding:1px 3px;}::-webkit-scrollbar{width:10px;height:10px;}::-webkit-scrollbar-corner{background-color:var(--vscode-editor-background);}::-webkit-scrollbar-thumb{background-color:var(--vscode-scrollbarSlider-background);}::-webkit-scrollbar-thumb:hover{background-color:var(--vscode-scrollbarSlider-hoverBackground);}::-webkit-scrollbar-thumb:active{background-color:var(--vscode-scrollbarSlider-activeBackground);}::highlight(find-highlight){background-color:var(--vscode-editor-findMatchHighlightBackground);}::highlight(current-find-highlight){background-color:var(--vscode-editor-findMatchBackground);}}";

	private static void Append(StringBuilder builder, string name, Color color) =>
		Append(builder, name, Css(color));

	private static void Append(StringBuilder builder, string name, string value) =>
		builder.Append("--vscode-").Append(name).Append(':').Append(value).Append(';');

	private static string Css(in Color color) {
		string hex = color.ToString();

		return hex.EndsWith("FF", StringComparison.Ordinal) ? hex[..7] : hex;
	}
}
