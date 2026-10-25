using Forge.Extensions.Dotnet.Server;

namespace Forge.Tests;

public class LanguageServerOptionsTests
{
	[Theory]
	[InlineData("csharp|background_analysis.dotnet_analyzer_diagnostics_scope", "dotnet.backgroundAnalysis.analyzerDiagnosticsScope")]
	[InlineData("csharp|inlay_hints.csharp_enable_inlay_hints_for_types", "csharp.inlayHints.enableInlayHintsForTypes")]
	[InlineData("navigation.dotnet_navigate_to_decompiled_sources", "dotnet.navigation.navigateToDecompiledSources")]
	[InlineData("csharp|implement_type.dotnet_insertion_behavior", "dotnet.implementType.insertionBehavior")]
	[InlineData("code_style.formatting.indentation_and_spacing.tab_width", "codeStyle.formatting.indentationAndSpacing.tabWidth")]
	public void ConvertsServerNamesToSettings(string section, string expected) => Assert.Equal(expected, LanguageServerOptions.ClientName(section));

	[Fact]
	public void IgnoresOtherLanguages() => Assert.Null(LanguageServerOptions.ClientName("visual_basic|formatting.dotnet_organize_imports_on_format"));
}
