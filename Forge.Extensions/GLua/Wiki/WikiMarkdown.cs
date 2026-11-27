using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Extensions.GLua.Wiki;

public static partial class WikiMarkdown
{
	public const string WikiUrl = "https://wiki.facepunch.com/gmod/";
	public const string GitHubUrl = "https://github.com/Facepunch/garrysmod/blob/master/garrysmod/";
	public const string GitHubRawUrl = "https://github.com/Facepunch/garrysmod/raw/master/garrysmod/";

	public static string? IconDirectory { get; set; }

	public static string SourceUrl((string Path, string Lines) source, bool raw) =>
		(raw ? GitHubRawUrl : GitHubUrl) + source.Path + "#" + string.Join("-", source.Lines.Split('-').Select(static line => "L" + line));

	public static string Escape(string text) {
		StringBuilder escaped = new(text.Length + 8);

		foreach (char character in text) {
			switch (character) {
				case '*' or '#' or '/' or '(' or ')' or '[' or ']' or '_': escaped.Append('\\').Append(character); break;
				case '<': escaped.Append("&lt;"); break;
				case '>': escaped.Append("&gt;"); break;
				default: escaped.Append(character); break;
			}
		}

		return escaped.ToString();
	}

	private static string MarkdownUrl(string url) => url.Replace("\\", "\\\\").Replace(" ", "%20");

	private static string FileUri(string path) => new Uri(path).AbsoluteUri;

	private static string Icon(string file, string alt) =>
		IconDirectory is null ? string.Empty : "![" + alt + "](" + MarkdownUrl(FileUri(Path.Combine(IconDirectory, file))) + ")";

	public static string RealmIcon(bool client, bool menu, bool server) {
		string id = (client ? "c" : "") + (menu ? "m" : "") + (server ? "s" : "");
		string alt = string.Join("/", new[] { client ? "CLIENT" : null, menu ? "MENU" : null, server ? "SERVER" : null }.Where(static realm => realm is not null));
		return Icon("realm_" + id + ".svg", alt);
	}

	public static string LabelIcon(string label) => Icon("label_" + label + ".svg", label.ToUpperInvariant());

	[GeneratedRegex(@"```[\s\S]+?```")]
	private static partial Regex CodeBlock();

	[GeneratedRegex(@"^[\t\r ]*", RegexOptions.Multiline)]
	private static partial Regex LeadingWhitespace();

	[GeneratedRegex(@"\[(.+?)\]\(\/gmod\/(.+?)\)")]
	private static partial Regex WikiLink();

	[GeneratedRegex(@"#(.*?)$")]
	private static partial Regex Anchor();

	[GeneratedRegex(@"^C[A-Z][a-z]")]
	private static partial Regex ClassType();

	public static string TypeEmoji(string type) {
		string emoji = type.ToLowerInvariant() switch {
			"number" => "🔢",
			"string" => "📋",
			"table" or "userdata" => "$(symbol-module)",
			"bool" or "boolean" => "🔰",
			"ent" or "entity" or "csent" => "🧱",
			"ply" or "player" => "🙂",
			"function" => "👨‍💻",
			"thread" => "🧵",
			"nil" => "⚫",
			"angle" => "📐",
			"vector" => "🔀",
			"material" or "texture" or "imaterial" or "itexture" => "🧩",
			"color" or "colour" => "🎨",
			"physobj" => "🍃",
			"panel" => "📱",
			"vehicle" => "🚗",
			"weapon" => "💣",
			"file" or "file_class" => "💾",
			"convar" => "🔌",
			"imesh" or "mesh" => "🌐",
			"npc" or "nextbot" => "🤖",
			"matrix" or "vmatrix" => "🧮",
			"tool" => "🔨",
			_ => string.Empty,
		};

		return emoji.Length == 0 && ClassType().IsMatch(type) ? "🔮" : emoji;
	}

	public static string Returns(IReadOnlyList<WikiDoc> returns) {
		StringBuilder markdown = new("**Returns**\n\n");

		foreach (WikiDoc value in returns) {
			string type = value.Type ?? string.Empty;
			markdown.Append(TypeEmoji(type)).Append('`').Append(type.Replace("`", "")).Append('`');

			if (value.Name is { } name) markdown.Append(" (").Append(name.Replace("`", "")).Append(')');

			if (!value.Description.IsEmpty) markdown.Append(' ').Append(value.Description.Value);

			markdown.Append("\n\n");
		}

		return markdown.ToString(0, markdown.Length - 2);
	}

	public static string Resolve(WikiDoc doc, string? label = null, bool compact = false) {
		List<string> markdown = [];

		if (!doc.BaseDescription.IsEmpty) markdown.Add(doc.BaseDescription.Value);

		List<string> flags = [];

		if (doc.HasRealm) flags.Add(RealmIcon(doc.Has(WikiFlags.Client), doc.Has(WikiFlags.Menu), doc.Has(WikiFlags.Server)));

		if (doc.Has(WikiFlags.New)) flags.Add(LabelIcon("new"));

		if (doc.Has(WikiFlags.Deprecated)) flags.Add(LabelIcon("deprecated"));

		if (doc.Has(WikiFlags.Internal)) flags.Add(LabelIcon("internal"));

		if (doc.Has(WikiFlags.ReferenceOnly)) flags.Add(LabelIcon("reference_only"));

		if (doc.Has(WikiFlags.Predicted)) flags.Add(LabelIcon("predicted"));

		if (doc.NetworkVar is not null) flags.Add(LabelIcon("networkvar"));

		if (flags.Count > 0) markdown.Add(string.Join(" ", flags));

		if (label is not null) markdown.Add("**" + Escape(label) + "**");

		if (!compact) {
			if (!doc.Description.IsEmpty) markdown.Add(doc.Description.Value);

			foreach (WikiBug bug in doc.Bugs ?? []) {
				string head = bug.Issue is { } issue ? " [🐞 **BUG: #" + issue + "**](https://github.com/facepunch/garrysmod-issues/issues/" + issue + ")"
					: bug.Pull is { } pull ? " [🐞 **BUG: PR #" + pull + "**](https://github.com/facepunch/garrysmod/pull/" + pull + ")"
					: "🐞 **BUG:**";
				markdown.Add(head + (bug.Description.IsEmpty ? "" : " " + bug.Description.Value));
			}

			foreach (WikiText note in doc.Notes ?? []) markdown.Add("**📝 NOTE:** " + note.Value);

			if (doc.Returns is { Length: > 0 } returns) markdown.Add("--------\n" + Returns(returns) + "\n\n--------");
		}
		else if (label is not null && markdown.Count > 1) {
			markdown[0] = markdown[0] + " " + markdown[1];
			markdown.RemoveAt(1);
		}

		foreach (WikiText warning in doc.Warnings ?? []) markdown.Add("**⚠️ WARNING:** " + warning.Value);

		List<(string Label, string Link)> links = [];

		if (doc.Link is { } link) {
			links.Add(("$(notebook) Wiki", WikiUrl + link));
			links.Add(("$(edit) Edit", WikiUrl + Anchor().Replace(link, "") + "~edit"));
		}

		if (doc.Source is { } source) links.Add(("$(source-control) View Source", SourceUrl(source, raw: false)));

		if (links.Count > 0) markdown.Add(string.Join(" | ", links.Select(static entry => "[" + Escape(entry.Label) + "](" + entry.Link + ")")));

		string joined = string.Join("\n\n", markdown);
		joined = CodeBlock().Replace(joined, "_< ommitted code block >_");
		joined = LeadingWhitespace().Replace(joined, "");
		return WikiLink().Replace(joined, "[$1](" + WikiUrl + "$2)");
	}
}
