using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Forge.Common.Commands;
using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Parsing;
using Forge.Extensions.GLua.Signatures;
using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Navigation;

public sealed partial class GLuaNavigation(GLuaCatalog catalog, GLuaIndex index, string tempRoot, ICommandHost host)
{
	private static readonly HttpClient Http = new();

	[GeneratedRegex(@"(?:\\(?:\d+|x[a-f0-9]+))+", RegexOptions.IgnoreCase)]
	private static partial Regex AsciiHover();

	[GeneratedRegex(@"(?:(""|')((?:\\\1|\\\\|.)*?)\1)|(?:\[(=*)\[([\s\S]*?)\]\3\])")]
	private static partial Regex LuaString();

	[GeneratedRegex(@"\\(.)")]
	private static partial Regex Escape();

	private const string WordSeparators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

	public static bool IsWordCharacter(char character) => !char.IsWhiteSpace(character) && !WordSeparators.Contains(character);

	public string? Hover(string path, string text, int line, int character) {
		List<string> hovers = [];
		string lineText = LineText(text, line);

		WikiHover(index.Get(path, text), line, character, hovers);

		if (StringHover(lineText, character) is { } stringHover) hovers.Add(stringHover);

		return hovers.Count == 0 ? null : string.Join(Forge.Language.HoverMarkdown.RowSeparator, hovers);
	}

	public static string LineText(string text, int line) {
		int start = 0;

		for (int i = 0; i < line; i++) {
			int next = text.IndexOf('\n', start);

			if (next < 0) return string.Empty;

			start = next + 1;
		}

		int end = text.IndexOf('\n', start);
		return text[start..(end < 0 ? text.Length : end)].TrimEnd('\r');
	}

	private void WikiHover(ParsedFile? file, int line, int character, List<string> hovers) {
		if (file?.TokenAt(line, character) is not { } found) return;

		for (LuaNode? token = found; token is not null; token = token.Parent) {
			if (token.Type == LuaNodeType.CallExpression) {
				(List<string> call, LuaNode target) = TokenAnalyzer.QualifyMemberExpression(token, line, character);

				for (LuaNode? parent = target.Parent; parent is not null; parent = parent.Parent) {
					if (parent != token) continue;

					LuaNode? expression = target.Type == LuaNodeType.CallExpression ? target : target.Parent?.Type == LuaNodeType.CallExpression ? target.Parent : null;

					if (expression is null) return;

					if (expression.Docs is { } docs) PushWikiHovers(hovers, docs);

					if (call.Count >= 2 && call[^2] == ":" && index.Compiled.SignatureMetaFunctions.TryGetValue(call[^1], out List<WikiDoc>? meta)) PushWikiHovers(hovers, meta);

					return;
				}
			}
			else if (token.Type == LuaNodeType.Identifier && !token.IsLocal && catalog.Docs.TryGetValue("ENUM:" + token.Name, out WikiDoc? enumDoc)) {
				PushWikiHovers(hovers, [enumDoc]);
				return;
			}
		}
	}

	private static void PushWikiHovers(List<string> hovers, IEnumerable<WikiDoc> docs) {
		foreach (WikiDoc doc in docs) {
			string signature = (doc.Has(WikiFlags.Event) ? "(hook) " : "") + (doc.Has(WikiFlags.Method) ? "(method) " : "") +
				(doc.Tag == "ENUM" ? "(enum) " + doc.Search : doc.Search + "(" + (doc.Arguments is { } arguments ? GLuaSignatures.SignatureString(arguments) : "") + ")");

			hovers.Add("```glua\n" + signature + "\n```");
			hovers.Add(WikiMarkdown.Resolve(doc));
		}
	}

	private static string? StringHover(string line, int character) {
		foreach (Match match in AsciiHover().Matches(line)) {
			if (character < match.Index || character > match.Index + match.Length) continue;

			StringBuilder bytes = new();

			foreach (string part in match.Value.Split('\\')) {
				if (part.Length == 0) continue;

				int value = part[0] is 'x' or 'X' ? int.Parse(part.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(part, CultureInfo.InvariantCulture);
				bytes.Append((char)value);
			}

			return "```glua\n" + bytes + "\n```";
		}

		foreach (Match match in LuaString().Matches(line)) {
			if (match.Groups[4].Success) continue;

			if (character < match.Index || character > match.Index + match.Length) continue;

			bool valid = true;
			string escaped = Escape().Replace(match.Groups[2].Value, escape => {
				if (LineTokenizer.EscapeSequences.TryGetValue(escape.Groups[1].Value[0], out string? replaced)) return replaced;

				valid = false;
				return string.Empty;
			});

			if (!valid) return "`invalid escape sequence`";

			int utf8 = Encoding.UTF8.GetByteCount(escaped);
			int cursor = match.Length - (match.Index + match.Length - character);

			return "Length: " + escaped.Length.ToString("N0", CultureInfo.CurrentCulture) + " bytes" +
				(utf8 != escaped.Length ? "\n\nUTF-8: " + utf8.ToString("N0", CultureInfo.CurrentCulture) + " characters" : "") +
				(cursor > 0 && cursor <= escaped.Length ? "\n\nPos: " + cursor : "");
		}

		return null;
	}

	private static (int Start, int End)? WordAt(string line, int character) {
		int start = Math.Min(character, line.Length);
		int end = start;

		while (start > 0 && IsWordCharacter(line[start - 1])) start--;

		while (end < line.Length && IsWordCharacter(line[end])) end++;

		return end > start ? (start, end) : null;
	}

	private static HostLocation Location(LuaNode node, string fallbackPath) {
		string path = node.Path ?? fallbackPath;
		return new HostLocation(path, node.StartLine, node.StartColumn + 1, node.EndLine, node.EndColumn + 1, Preview(path, node.StartLine));
	}

	private static string Preview(string path, int line) {
		try {
			return File.ReadLines(path).Skip(line - 1).FirstOrDefault()?.Trim() ?? string.Empty;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return string.Empty;
		}
	}

	public async Task<IReadOnlyList<HostLocation>> DefinitionAsync(string path, string text, int line, int character) {
		if (WordAt(LineText(text, line), character) is null || index.Get(path, text)?.TokenAt(line, character) is not { } token) return [];

		List<HostLocation> definitions = [];
		List<Task<HostLocation?>> downloads = [];

		switch (token.Type) {
			case LuaNodeType.MemberExpression:
			case LuaNodeType.Identifier:
				(List<string> call, LuaNode target) = TokenAnalyzer.QualifyMemberExpression(token, line, character);
				bool isLocal = false;

				for (LuaNode node = target; node.HasBase && node.Base is not null;) {
					if (node.IsLocal) {
						isLocal = true;
						break;
					}

					if (node.Base.HasBase) node = node.Base;
					else break;
				}

				Definitions(call, token, isLocal ? line : null, path, definitions, downloads);
				break;
			case LuaNodeType.StringLiteral:
			case LuaNodeType.BinaryExpression:
				if (token.Parent is not { } parent || TokenAnalyzer.FullFunctionCall(parent) is not { Count: 3 } parentCall) break;

				if (string.Concat(parentCall) is "net.Receive" or "net.Start" or "util.AddNetworkString" && token.Text is { } value && index.Compiled.DefinitionNetworkStrings.TryGetValue(value, out List<LuaNode>? receivers)) {
					foreach (LuaNode receiver in receivers) definitions.Add(Location(receiver, parent.Path ?? path));
				}

				break;
		}

		foreach (Task<HostLocation?> download in downloads) {
			if (await download.ConfigureAwait(false) is { } location) definitions.Add(location);
		}

		return definitions;
	}

	private void Push(IEnumerable<LuaNode> nodes, int? line, string path, List<HostLocation> into) {
		foreach (LuaNode node in nodes) {
			if (line is { } limit && node.StartLine > limit) continue;

			into.Add(Location(node, path));
		}
	}

	private void Definitions(List<string> call, LuaNode token, int? line, string path, List<HostLocation> definitions, List<Task<HostLocation?>> downloads) {
		if (call.Count == 0) return;

		string full = string.Concat(call);
		FileTokenData compiled = index.Compiled;

		if (call.Count == 1) {
			for (LuaNode? parent = token; parent is not null; parent = parent.Parent) {
				if (parent.Scope?.TryGetValue(full, out LuaNode? scoped) == true) {
					Push([scoped], line, path, definitions);
					break;
				}
			}
		}

		if ((call.Count < 3 || call[^2] != ":") && compiled.GlobalTokens.TryGetValue(full, out List<LuaNode>? globals)) Push(globals, line, path, definitions);

		if (call.Count == 1) {
			if (catalog.Docs.TryGetValue("GLOBAL:" + call[0], out WikiDoc? global) && global.Source is { } source) downloads.Add(GitHubAsync(source));

			return;
		}

		if (call.Count < 3) return;

		if (compiled.DefinitionMetaFunctions.TryGetValue(call[^1], out List<LuaNode>? meta)) Push(meta, null, path, definitions);

		if (catalog.Docs.TryGetValue("FUNCTION:" + full, out WikiDoc? function) && function.Source is { } functionSource) downloads.Add(GitHubAsync(functionSource));

		if (catalog.Docs.TryGetValue("META_FUNCTION:" + full, out WikiDoc? method) && method.Source is { } methodSource) downloads.Add(GitHubAsync(methodSource));
	}

	private async Task<HostLocation?> GitHubAsync((string Path, string Lines) source) {
		string target = Path.Combine(tempRoot, "Facepunch", "garrysmod", source.Path.Replace('/', Path.DirectorySeparatorChar));
		FileInfo info = new(target);

		if (!info.Exists || info.Length == 0 || DateTime.Now - info.LastWriteTime >= TimeSpan.FromDays(1)) {
			const string StatusId = "glua.download";

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				host.SetStatusItem(StatusId, "cloud-download", "Downloading source from GitHub... 0%", null);

				using HttpResponseMessage response = await Http.GetAsync(WikiMarkdown.SourceUrl(source, raw: true), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

				if (!response.IsSuccessStatusCode) {
					host.ShowMessage(MessageSeverity.Error, "Error: " + (int)response.StatusCode + " " + response.ReasonPhrase, []);
					return null;
				}

				long total = response.Content.Headers.ContentLength ?? 0;
				string partial = target + ".download";

				await using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
				await using (FileStream output = File.Create(partial)) {
					byte[] buffer = new byte[81920];
					long received = 0;
					int read;

					while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0) {
						await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
						received += read;

						if (total > 0) host.SetStatusItem(StatusId, "cloud-download", "Downloading source from GitHub... " + (received * 100 / total) + "%", null);
					}
				}

				File.Move(partial, target, overwrite: true);
			}
			catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException) {
				host.ShowMessage(MessageSeverity.Error, "Error: " + exception.Message, []);
				return null;
			}
			finally {
				host.RemoveStatusItem(StatusId);
			}
		}

		string[] lines = source.Lines.Split('-');
		int startLine = int.TryParse(lines[0], out int first) ? first : 1;
		int endLine = lines.Length > 1 && int.TryParse(lines[1], out int last) ? last : startLine;

		return new HostLocation(target, startLine, 1, endLine, lines.Length > 1 ? int.MaxValue : 1, Preview(target, startLine));
	}

	public IReadOnlyList<HostLocation> References(string path, string text, int line, int character) {
		if (WordAt(LineText(text, line), character) is null) return [];

		if (index.Get(path, text)?.TokenAt(line, character, LuaNodeType.StringLiteral) is not { Parent: { } parent } token) return [];

		if (TokenAnalyzer.FullFunctionCall(parent) is not { Count: 3 } call || string.Concat(call) is not ("net.Receive" or "net.Start" or "util.AddNetworkString")) return [];

		if (token.Text is not { } value || !index.Compiled.ReferenceNetworkStrings.TryGetValue(value, out List<LuaNode>? references)) return [];

		return [.. references.Select(reference => Location(reference, path))];
	}
}
