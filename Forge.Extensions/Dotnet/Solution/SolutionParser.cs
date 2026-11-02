using System.Text.Json;
using System.Xml;

namespace Forge.Extensions.Dotnet.Solution;

public enum SolutionNodeKind
{
	Folder,
	Project,
	File,
}

public sealed class SolutionNode(SolutionNodeKind kind, string name, string? path = null)
{
	public SolutionNodeKind Kind { get; } = kind;

	public string Name { get; } = name;

	public string? Path { get; } = path;

	public List<SolutionNode> Children { get; } = [];
}

public sealed record SolutionModel(string Path, string Name, List<SolutionNode> Roots);

public static class SolutionParser
{
	private const string SolutionFolderType = "{2150E333-8FDC-42A3-9474-1A3956D46DE8}";

	public static SolutionModel Parse(string solutionPath) {
		ReadOnlySpan<char> extension = System.IO.Path.GetExtension(solutionPath.AsSpan());
		string name = System.IO.Path.GetFileNameWithoutExtension(solutionPath);
		string baseDirectory = System.IO.Path.GetDirectoryName(solutionPath) ?? string.Empty;

		if (extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase)) return ParseFilter(solutionPath);

		List<SolutionNode> roots = extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
			? ParseSlnx(File.ReadAllText(solutionPath), baseDirectory)
			: ParseSln(File.ReadAllText(solutionPath), baseDirectory);

		Sort(roots);
		return new SolutionModel(solutionPath, name, roots);
	}

	private static SolutionModel ParseFilter(string filterPath) {
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(filterPath));

		if (!document.RootElement.TryGetProperty("solution", out JsonElement solution) || !solution.TryGetProperty("path", out JsonElement target) || target.GetString() is not { } relative) throw new InvalidDataException($"{System.IO.Path.GetFileName(filterPath)} does not name a solution.");

		string solutionPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filterPath) ?? string.Empty, Normalize(relative)));
		SolutionModel parsed = Parse(solutionPath);
		string solutionDirectory = System.IO.Path.GetDirectoryName(solutionPath) ?? string.Empty;
		HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);

		if (solution.TryGetProperty("projects", out JsonElement projects)) {
			foreach (JsonElement project in projects.EnumerateArray()) {
				if (project.GetString() is { } path) included.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDirectory, Normalize(path))));
			}
		}

		return new SolutionModel(filterPath, System.IO.Path.GetFileNameWithoutExtension(filterPath), Prune(parsed.Roots, included));
	}

	private static List<SolutionNode> Prune(List<SolutionNode> nodes, HashSet<string> included) {
		List<SolutionNode> result = [];

		foreach (SolutionNode node in nodes) {
			switch (node.Kind) {
				case SolutionNodeKind.Project:
					if (node.Path is { } path && included.Contains(path)) result.Add(node);
					break;

				case SolutionNodeKind.Folder:
					List<SolutionNode> children = Prune(node.Children, included);

					if (children.Count == 0) break;

					SolutionNode folder = new(SolutionNodeKind.Folder, node.Name);
					folder.Children.AddRange(children);
					result.Add(folder);
					break;

				default:
					result.Add(node);
					break;
			}
		}

		return result;
	}

	public static List<SolutionNode> ParseSln(string text, string baseDirectory) {
		Dictionary<string, (SolutionNode Node, bool Folder)> entries = new(StringComparer.OrdinalIgnoreCase);
		List<string> order = [];
		Dictionary<string, string> nesting = new(StringComparer.OrdinalIgnoreCase);
		ReadOnlySpan<char> remaining = text;

		while (NextLine(ref remaining, out ReadOnlySpan<char> line)) {
			if (line.StartsWith("Project(\"", StringComparison.Ordinal)) {
				if (!ParseProjectHeader(line, out ReadOnlySpan<char> type, out ReadOnlySpan<char> name, out ReadOnlySpan<char> relative, out ReadOnlySpan<char> id)) continue;

				bool folder = type.Equals(SolutionFolderType, StringComparison.OrdinalIgnoreCase);
				SolutionNode node = folder
					? new SolutionNode(SolutionNodeKind.Folder, name.ToString())
					: new SolutionNode(SolutionNodeKind.Project, name.ToString(), Resolve(baseDirectory, relative));

				bool items = false;

				while (NextLine(ref remaining, out ReadOnlySpan<char> body)) {
					if (body.SequenceEqual("EndProject")) break;

					if (body.StartsWith("ProjectSection(SolutionItems)", StringComparison.Ordinal)) items = true;
					else if (body.SequenceEqual("EndProjectSection")) items = false;
					else if (items && folder) {
						int equals = body.IndexOf('=');

						if (equals <= 0) continue;

						string path = Resolve(baseDirectory, body[..equals].Trim());
						node.Children.Add(new SolutionNode(SolutionNodeKind.File, System.IO.Path.GetFileName(path), path));
					}
				}

				string key = id.ToString();
				entries[key] = (node, folder);
				order.Add(key);
				continue;
			}

			if (!line.StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal)) continue;

			while (NextLine(ref remaining, out ReadOnlySpan<char> pair)) {
				if (pair.SequenceEqual("EndGlobalSection")) break;

				int equals = pair.IndexOf('=');

				if (equals <= 0) continue;

				ReadOnlySpan<char> child = pair[..equals].Trim();
				ReadOnlySpan<char> parent = pair[(equals + 1)..].Trim();

				if (child.Length > 1 && child[0] == '{' && parent.Length > 1 && parent[0] == '{') nesting[child.ToString()] = parent.ToString();
			}
		}

		List<SolutionNode> roots = [];

		foreach (string id in order) {
			SolutionNode node = entries[id].Node;

			if (nesting.TryGetValue(id, out string? parentId) && entries.TryGetValue(parentId, out (SolutionNode Node, bool Folder) parent) && parent.Folder) parent.Node.Children.Add(node);
			else roots.Add(node);
		}

		return roots;
	}

	private static bool ParseProjectHeader(ReadOnlySpan<char> line, out ReadOnlySpan<char> type, out ReadOnlySpan<char> name, out ReadOnlySpan<char> relative, out ReadOnlySpan<char> id) {
		type = name = relative = id = default;
		ReadOnlySpan<char> rest = line["Project(\"".Length..];

		if (!TakeQuoted(ref rest, out type, closingPrefix: true)) return false;

		int equals = rest.IndexOf('=');

		if (equals < 0) return false;

		rest = rest[(equals + 1)..];

		return TakeQuoted(ref rest, out name) && TakeQuoted(ref rest, out relative) && TakeQuoted(ref rest, out id);
	}

	private static bool TakeQuoted(scoped ref ReadOnlySpan<char> text, out ReadOnlySpan<char> value, bool closingPrefix = false) {
		value = default;

		if (!closingPrefix) {
			int open = text.IndexOf('"');

			if (open < 0) return false;

			text = text[(open + 1)..];
		}

		int close = text.IndexOf('"');

		if (close < 0) return false;

		value = text[..close];
		text = text[(close + 1)..];
		return true;
	}

	private static bool NextLine(ref ReadOnlySpan<char> text, out ReadOnlySpan<char> line) {
		if (text.IsEmpty) {
			line = default;
			return false;
		}

		int end = text.IndexOfAny('\r', '\n');

		if (end < 0) {
			line = text.Trim();
			text = default;
			return true;
		}

		line = text[..end].Trim();
		text = text[(end + 1)..];

		if (!text.IsEmpty && text[0] == '\n' && end >= 0) text = text[1..];

		return true;
	}

	public static List<SolutionNode> ParseSlnx(string text, string baseDirectory) {
		List<SolutionNode> roots = [];
		Dictionary<string, SolutionNode> folders = new(StringComparer.Ordinal);
		Stack<SolutionNode?> stack = new();

		using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true });

		while (reader.Read()) {
			if (reader.NodeType == XmlNodeType.EndElement) {
				if (reader.LocalName == "Folder" && stack.Count > 0) stack.Pop();

				continue;
			}

			if (reader.NodeType != XmlNodeType.Element) continue;

			switch (reader.LocalName) {
				case "Folder": {
						SolutionNode folder = FolderByPath(reader.GetAttribute("Name") ?? string.Empty, roots, folders);

						if (!reader.IsEmptyElement) stack.Push(folder);

						break;
					}

				case "Project" or "File": {
						if (reader.GetAttribute("Path") is not { } relative) break;

						string path = Resolve(baseDirectory, relative);
						string? display = reader.GetAttribute("DisplayName");
						SolutionNode node = reader.LocalName == "Project"
							? new SolutionNode(SolutionNodeKind.Project, display ?? System.IO.Path.GetFileNameWithoutExtension(path), path)
							: new SolutionNode(SolutionNodeKind.File, display ?? System.IO.Path.GetFileName(path), path);

						(stack.Count > 0 && stack.Peek() is { } parent ? parent.Children : roots).Add(node);
						break;
					}
			}
		}

		return roots;
	}

	private static SolutionNode FolderByPath(string folderPath, List<SolutionNode> roots, Dictionary<string, SolutionNode> folders) {
		List<SolutionNode> container = roots;
		SolutionNode? folder = null;
		int end = 0;

		foreach (Range range in folderPath.AsSpan().Split('/')) {
			ReadOnlySpan<char> segment = folderPath.AsSpan()[range];

			if (segment.IsEmpty) continue;

			end = range.End.GetOffset(folderPath.Length);
			string key = folderPath[..end];

			if (!folders.TryGetValue(key, out SolutionNode? existing)) {
				existing = new SolutionNode(SolutionNodeKind.Folder, segment.ToString());
				folders[key] = existing;
				container.Add(existing);
			}

			folder = existing;
			container = existing.Children;
		}

		return folder ?? new SolutionNode(SolutionNodeKind.Folder, folderPath);
	}

	private static string Resolve(string baseDirectory, ReadOnlySpan<char> relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDirectory, Normalize(relative)));

	private static string Normalize(ReadOnlySpan<char> path) => string.Create(path.Length, path, static (span, source) => {
		for (int i = 0; i < span.Length; i++) span[i] = source[i] is '\\' or '/' ? System.IO.Path.DirectorySeparatorChar : source[i];
	});

	private static void Sort(List<SolutionNode> nodes) {
		nodes.Sort(static (left, right) => {
			int rank = Rank(left.Kind).CompareTo(Rank(right.Kind));
			return rank != 0 ? rank : string.Compare(left.Name, right.Name, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);
		});

		foreach (SolutionNode node in nodes) {
			if (node.Kind == SolutionNodeKind.Folder) Sort(node.Children);
		}
	}

	private static int Rank(SolutionNodeKind kind) => kind switch {
		SolutionNodeKind.Folder => 0,
		SolutionNodeKind.Project => 1,
		_ => 2,
	};
}
