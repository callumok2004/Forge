using System.Text.Json.Nodes;

namespace Forge.Extensions.Dotnet.Server;

internal static class ClientCapabilities
{
	public static JsonObject Create() => new() {
		["textDocument"] = new JsonObject {
			["synchronization"] = new JsonObject {
				["dynamicRegistration"] = false,
				["didSave"] = true,
			},
			["publishDiagnostics"] = new JsonObject {
				["relatedInformation"] = false,
			},
			["completion"] = new JsonObject {
				["completionItem"] = new JsonObject {
					["snippetSupport"] = false,
					["labelDetailsSupport"] = true,
					["resolveSupport"] = new JsonObject {
						["properties"] = new JsonArray("documentation", "detail", "additionalTextEdits"),
					},
				},
			},
			["hover"] = new JsonObject {
				["contentFormat"] = new JsonArray("plaintext", "markdown"),
			},
			["definition"] = new JsonObject { ["dynamicRegistration"] = false },
			["implementation"] = new JsonObject { ["dynamicRegistration"] = false },
			["typeDefinition"] = new JsonObject { ["dynamicRegistration"] = false },
			["references"] = new JsonObject { ["dynamicRegistration"] = false },
			["formatting"] = new JsonObject { ["dynamicRegistration"] = false },
			["rangeFormatting"] = new JsonObject { ["dynamicRegistration"] = false },
			["rename"] = new JsonObject { ["dynamicRegistration"] = false, ["prepareSupport"] = true },
			["documentSymbol"] = new JsonObject {
				["dynamicRegistration"] = false,
				["hierarchicalDocumentSymbolSupport"] = true,
			},
			["codeAction"] = new JsonObject {
				["dynamicRegistration"] = false,
				["isPreferredSupport"] = true,
				["disabledSupport"] = true,
				["dataSupport"] = true,
				["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") },
				["codeActionLiteralSupport"] = new JsonObject {
					["codeActionKind"] = new JsonObject {
						["valueSet"] = new JsonArray(string.Empty, "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports"),
					},
				},
			},
			["signatureHelp"] = new JsonObject {
				["dynamicRegistration"] = false,
				["contextSupport"] = true,
				["signatureInformation"] = new JsonObject {
					["documentationFormat"] = new JsonArray("markdown", "plaintext"),
					["parameterInformation"] = new JsonObject { ["labelOffsetSupport"] = true },
					["activeParameterSupport"] = true,
				},
			},
			["onTypeFormatting"] = new JsonObject { ["dynamicRegistration"] = false },
			["documentHighlight"] = new JsonObject { ["dynamicRegistration"] = false },
			["foldingRange"] = new JsonObject { ["dynamicRegistration"] = false, ["lineFoldingOnly"] = true },
			["inlayHint"] = new JsonObject { ["dynamicRegistration"] = false, ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("tooltip", "label.tooltip", "label.location", "label.command") } },
			["codeLens"] = new JsonObject { ["dynamicRegistration"] = false },
			["semanticTokens"] = new JsonObject {
				["dynamicRegistration"] = false,
				["requests"] = new JsonObject { ["full"] = true },
				["tokenTypes"] = new JsonArray(),
				["tokenModifiers"] = new JsonArray(),
				["formats"] = new JsonArray("relative"),
			},
			["diagnostic"] = new JsonObject {
				["dynamicRegistration"] = false,
				["relatedDocumentSupport"] = false,
			},
		},
		["workspace"] = new JsonObject {
			["applyEdit"] = true,
			["workspaceEdit"] = new JsonObject {
				["documentChanges"] = true,
				["resourceOperations"] = new JsonArray("create", "rename", "delete"),
			},
			["configuration"] = true,
			["workspaceFolders"] = true,
			["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = false },
			["diagnostics"] = new JsonObject { ["refreshSupport"] = true },
			["semanticTokens"] = new JsonObject { ["refreshSupport"] = true },
			["codeLens"] = new JsonObject { ["refreshSupport"] = true },
		},
		["window"] = new JsonObject {
			["workDoneProgress"] = true,
		},
	};
}
