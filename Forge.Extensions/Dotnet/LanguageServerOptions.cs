using System.Text.Json.Nodes;

using Forge.Common.Extensions;
using Forge.Core;
using Forge.Extensions.Host;

namespace Forge.Extensions.Dotnet;

public static class LanguageServerOptions
{
	private static readonly (string Section, string Setting, JsonNode Default)[] Options =
	[
		("csharp|background_analysis.dotnet_analyzer_diagnostics_scope",
			"dotnet.backgroundAnalysis.analyzerDiagnosticsScope", "openFiles"),

		("csharp|background_analysis.dotnet_compiler_diagnostics_scope",
			"dotnet.backgroundAnalysis.compilerDiagnosticsScope", "openFiles"),

		("csharp|code_lens.dotnet_enable_references_code_lens",
			"dotnet.codeLens.enableReferencesCodeLens", true),

		("csharp|code_lens.dotnet_enable_tests_code_lens",
			"dotnet.codeLens.enableTestsCodeLens", true),

		("csharp|completion.dotnet_show_completion_items_from_unimported_namespaces",
			"dotnet.completion.showCompletionItemsFromUnimportedNamespaces", true),

		("csharp|completion.dotnet_show_name_completion_suggestions",
			"dotnet.completion.showNameCompletionSuggestions", true),

		("csharp|formatting.dotnet_organize_imports_on_format",
			"dotnet.formatting.organizeImportsOnFormat", false),

		("csharp|highlighting.dotnet_highlight_related_json_components",
			"dotnet.highlighting.highlightRelatedJsonComponents", true),

		("csharp|highlighting.dotnet_highlight_related_regex_components",
			"dotnet.highlighting.highlightRelatedRegexComponents", true),

		("csharp|inlay_hints.csharp_enable_inlay_hints_for_types",
			"dotnet.inlayHints.enableInlayHintsForTypes", false),

		("csharp|inlay_hints.dotnet_enable_inlay_hints_for_parameters",
			"dotnet.inlayHints.enableInlayHintsForParameters", false),

		("csharp|quick_info.dotnet_show_remarks_in_quick_info",
			"dotnet.quickInfo.showRemarksInQuickInfo", true),

		("csharp|symbol_search.dotnet_search_reference_assemblies",
			"dotnet.symbolSearch.searchReferenceAssemblies", true),

		("navigation|dotnet_navigate_to_decompiled_sources",
			"dotnet.navigation.navigateToDecompiledSources", true),
	];

	public static JsonArray Resolve(JsonNode? parameters, Core.Settings? settings) {
		JsonArray answer = [];

		foreach (JsonNode? item in parameters?["items"] as JsonArray ?? []) {
			answer.Add(Value(item?["section"]?.GetValue<string>(), settings));
		}

		return answer;
	}

	private static JsonNode? Value(string? section, Core.Settings? settings) {
		if (section is null) return null;

		foreach ((string name, string setting, JsonNode fallback) in Options) {
			if (!string.Equals(name, section, StringComparison.Ordinal)) continue;

			if (fallback.GetValueKind() == System.Text.Json.JsonValueKind.String) {
				return settings?.GetString(setting) is { } text
					? JsonValue.Create(text)
					: fallback.DeepClone();
			}

			return settings?.GetBoolean(setting) is { } flag
				? JsonValue.Create(flag)
				: fallback.DeepClone();
		}

		return null;
	}
}
