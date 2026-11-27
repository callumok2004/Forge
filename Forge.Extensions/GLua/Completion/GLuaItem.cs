using Forge.Extensions.GLua.Wiki;

namespace Forge.Extensions.GLua.Completion;

public sealed class GLuaItem(string label, string kind)
{
	public string Label { get; init; } = label;

	public string Kind { get; } = kind;

	public string? InsertText { get; set; }

	public string? FilterText { get; set; }

	public string? SortText { get; set; }

	public string? Detail { get; set; }

	public bool Deprecated { get; set; }

	public bool NoDoc { get; set; }

	public string? DocTag { get; set; }

	public WikiDoc? Doc { get; init; }

	public string? Documentation { get; init; }

	public int ReplaceStartColumn { get; set; }

	public GLuaItem With(string label, string? insertText) => new(label, Kind) {
		InsertText = insertText, FilterText = FilterText, SortText = SortText, Detail = Detail, Deprecated = Deprecated, NoDoc = NoDoc, DocTag = DocTag, Doc = Doc, Documentation = Documentation,
	};
}

public sealed class GLuaList(bool incomplete = false)
{
	public List<GLuaItem> Items { get; } = [];

	public bool IsIncomplete { get; } = incomplete;

	public Dictionary<string, GLuaList>? Children { get; private set; }

	public GLuaList Child(string key, bool incomplete = false) {
		Children ??= [with(StringComparer.Ordinal)];

		if (!Children.TryGetValue(key, out GLuaList? child)) Children[key] = child = new GLuaList(incomplete);

		return child;
	}

	public GLuaList? Find(string key) => Children is not null && Children.TryGetValue(key, out GLuaList? child) ? child : null;

	public bool Has(string key) => Children?.ContainsKey(key) == true;

	private HashSet<(string Kind, string Label)>? _unique;

	public bool AddUnique(GLuaItem item) {
		_unique ??= [];

		if (!_unique.Add((item.Kind, item.Label))) return false;

		Items.Add(item);
		return true;
	}

	public void Merge(GLuaList source) {
		foreach (GLuaItem item in source.Items) AddUnique(item);

		if (source.Children is null) return;

		foreach ((string key, GLuaList child) in source.Children) Child(key, child.IsIncomplete).Merge(child);
	}
}
