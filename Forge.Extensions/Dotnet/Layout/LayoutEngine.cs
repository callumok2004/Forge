using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Forge.Extensions.Dotnet.Layout;

public enum LayoutKind
{
	Struct,
	Class,
	Enum,
	Reference,
	Primitive,
	Unknown,
}

public enum LayoutMode
{
	None,
	Sequential,
	Auto,
	Explicit,
	InlineArray,
}

public readonly record struct FieldLayout(string Name, string TypeText, int Offset, int Size, int Align, bool IsReference, int PadBefore, bool Exact, int NameOffset);

public readonly record struct ReorderField(string Name, string TypeText, int Offset, int Size);

public sealed record Reorder(int Size, int Saved, ReorderField[] Order);

public sealed record TypeLayout
{
	public required string Name { get; init; }

	public required LayoutKind Kind { get; init; }

	public int Size { get; init; }

	public int Align { get; init; }

	public bool HasReferences { get; init; }

	public bool Exact { get; init; } = true;

	public LayoutMode Mode { get; init; }

	public FieldLayout[] Fields { get; init; } = [];

	public int Padding { get; init; }

	public int TailPadding { get; init; }

	public int HeapSize { get; init; } = -1;

	public int BaseSize { get; init; }

	public string? BaseName { get; init; }

	public Reorder? Reorder { get; init; }

	public string? Note { get; init; }
}

public readonly record struct DeclarationLayout(TypeDeclaration Declaration, int Start, TypeLayout Layout);

public readonly record struct DefinitionTarget(string Path, int Line, int Column);

public delegate Task<DefinitionTarget?> DefinitionLookup(string path, int line, int column);

public sealed class LayoutEngine(DefinitionLookup? lookup, Func<string, string?> openText, Func<string, bool> inWorkspace)
{
	public const int Pointer = 8;
	public const int ObjectHeader = 16;
	public const int MinimumObject = 24;
	private const int MaximumDepth = 12;

	private static readonly FrozenDictionary<string, TypeLayout> Primitives = BuildPrimitives();
	private static readonly FrozenSet<string> EnumBases = FrozenSet.ToFrozenSet(["byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char"]);
	private static readonly FrozenSet<string> ReferenceNames = FrozenSet.ToFrozenSet(["string", "object", "dynamic", "String", "Object", "System.String", "System.Object", "Type", "Delegate", "Action", "Func", "Exception"]);

	private readonly ConcurrentDictionary<string, DocumentInfo> _documents = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Task<TypeLayout>> _layouts = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, Task<DefinitionTarget?>> _targets = new(StringComparer.OrdinalIgnoreCase);
	private readonly ConcurrentDictionary<string, string> _targetDocuments = new(StringComparer.OrdinalIgnoreCase);

	public sealed class DocumentInfo(int hash, int length, string blanked, List<(TypeDeclaration Declaration, int Start)> declarations)
	{
		public int Hash { get; } = hash;

		public int Length { get; } = length;

		public string Blanked { get; } = blanked;

		public List<(TypeDeclaration Declaration, int Start)> Declarations { get; } = declarations;

		private int[]? _lineStarts;

		public int[] LineStarts => _lineStarts ??= ComputeLineStarts(Blanked);

		public int LineOf(int offset) {
			int index = Array.BinarySearch(LineStarts, offset);

			return index >= 0 ? index : ~index - 1;
		}

		public int OffsetOf(int line, int column) {
			int[] starts = LineStarts;

			return (uint)line < (uint)starts.Length ? Math.Min(Blanked.Length, starts[line] + column) : Blanked.Length;
		}
	}

	private readonly record struct Context(string? Path, string Blanked, Dictionary<string, TypeLayout> Substitutions, int Depth, HashSet<string> Visiting);

	private static FrozenDictionary<string, TypeLayout> BuildPrimitives() {
		Dictionary<string, TypeLayout> primitives = [];

		foreach (string name in (ReadOnlySpan<string>)["Span", "ReadOnlySpan", "Memory", "ReadOnlyMemory", "ArraySegment"]) primitives[name] = Primitive(name, 16, 8) with { Kind = LayoutKind.Struct, HasReferences = true };

		Add(["bool", "Boolean", "System.Boolean", "byte", "Byte", "System.Byte", "sbyte", "SByte", "System.SByte"], 1, 1);
		Add(["char", "Char", "System.Char", "short", "Int16", "System.Int16", "ushort", "UInt16", "System.UInt16", "Half", "System.Half"], 2, 2);
		Add(["int", "Int32", "System.Int32", "uint", "UInt32", "System.UInt32", "float", "Single", "System.Single"], 4, 4);
		Add(["long", "Int64", "System.Int64", "ulong", "UInt64", "System.UInt64", "double", "Double", "System.Double", "nint", "nuint", "IntPtr", "UIntPtr", "System.IntPtr", "System.UIntPtr", "DateTime", "TimeSpan", "DateOnly", "TimeOnly"], 8, 8);
		Add(["decimal", "Decimal", "System.Decimal"], 16, 8);
		Add(["Guid", "System.Guid"], 16, 4);
		Add(["Int128", "UInt128"], 16, 16);
		Add(["DateTimeOffset"], 16, 8);
		Add(["Vector2", "System.Numerics.Vector2"], 8, 4);
		Add(["Vector3", "System.Numerics.Vector3"], 12, 4);
		Add(["Vector4", "System.Numerics.Vector4", "Quaternion", "System.Numerics.Quaternion", "Plane", "System.Numerics.Plane"], 16, 4);
		Add(["Matrix3x2", "System.Numerics.Matrix3x2"], 24, 4);
		Add(["Matrix4x4", "System.Numerics.Matrix4x4"], 64, 4);
		Add(["Vector128"], 16, 16);
		Add(["Vector256"], 32, 32);
		Add(["CancellationToken", "GCHandle"], 8, 8);

		return primitives.ToFrozenDictionary();

		void Add(ReadOnlySpan<string> names, int size, int align) {
			foreach (string name in names) primitives[name] = Primitive(name, size, align);
		}
	}

	private static TypeLayout Primitive(string name, int size, int align) => new() { Name = name, Kind = LayoutKind.Primitive, Size = size, Align = align };

	private static TypeLayout ReferenceLayout(string name) => new() { Name = name, Kind = LayoutKind.Reference, Size = Pointer, Align = Pointer, HasReferences = true };

	private static TypeLayout Unknown(string name, string note) => new() { Name = name, Kind = LayoutKind.Unknown, Size = Pointer, Align = Pointer, Exact = false, Note = note };

	private static TypeLayout ExternalClass(string name, bool exact = false) => new() { Name = name, Kind = LayoutKind.Class, Size = 0, Align = 1, HasReferences = true, Exact = exact };

	private static int AlignUp(int value, int align) => align <= 1 ? value : (value + align - 1) / align * align;

	public static TypeLayout StoredAs(TypeLayout layout) => layout.Kind == LayoutKind.Class ? ReferenceLayout(layout.Name) : layout;

	private static int[] ComputeLineStarts(string text) {
		List<int> starts = [0];
		int index = 0;

		while ((index = text.IndexOf('\n', index)) >= 0) starts.Add(++index);

		return [.. starts];
	}

	public void Invalidate() {
		_layouts.Clear();
		_targets.Clear();
		_targetDocuments.Clear();
	}

	public void Forget(string path) {
		_documents.TryRemove(path, out _);

		foreach (string key in _layouts.Keys) {
			if (key.Length > path.Length && key[path.Length] == '#' && key.StartsWith(path, StringComparison.OrdinalIgnoreCase)) _layouts.TryRemove(key, out _);
		}

		foreach (string key in _targets.Keys) {
			bool from = key.Length > path.Length && key[path.Length] == '|' && key.StartsWith(path, StringComparison.OrdinalIgnoreCase);

			if (!from && !(_targetDocuments.TryGetValue(key, out string? landed) && string.Equals(landed, path, StringComparison.OrdinalIgnoreCase))) continue;

			_targets.TryRemove(key, out _);
			_targetDocuments.TryRemove(key, out _);
		}
	}

	public DocumentInfo? Document(string path) {
		string? text = openText(path);

		if (text is null) {
			try {
				text = File.ReadAllText(path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
				return null;
			}
		}

		int hash = string.GetHashCode(text);

		if (_documents.TryGetValue(path, out DocumentInfo? cached) && cached.Hash == hash && cached.Length == text.Length) return cached;

		string blanked = CSharpSource.Blank(text);
		DocumentInfo info = new(hash, text.Length, blanked, CSharpSource.ScanTypeDeclarations(blanked));
		_documents[path] = info;

		return info;
	}

	public async Task<List<DeclarationLayout>> LayoutsInAsync(string path, CancellationToken cancellation) {
		List<DeclarationLayout> results = [];

		if (Document(path) is not { } info) return results;

		foreach ((TypeDeclaration declaration, int start) in info.Declarations) {
			if (cancellation.IsCancellationRequested) return results;

			if (declaration.TypeParameters.Count > 0 || declaration.Kind == TypeKind.Interface) continue;

			results.Add(new DeclarationLayout(declaration, start, await LayoutOfDeclarationAsync(path, info.Blanked, declaration, [], 0, []).ConfigureAwait(false)));
		}

		return results;
	}

	public Task<TypeLayout> LayoutAtAsync(string path, int offset, string text) => Document(path) is { } info ? ResolveTypeAsync(text, offset, new Context(path, info.Blanked, [], 0, [])) : Task.FromResult(Unknown(text, "document unavailable"));

	public async Task<TypeLayout> LayoutOfDeclarationAsync(string? path, string blanked, TypeDeclaration declaration, Dictionary<string, TypeLayout> substitutions, int depth, HashSet<string> visiting) {
		Context context = new(path, blanked, substitutions, depth, visiting);

		if (declaration.Kind == TypeKind.Enum) {
			string? baseName = declaration.EnumBase?.Text;
			TypeLayout primitive = baseName is not null && EnumBases.Contains(baseName) ? Primitives[baseName] : Primitives["int"];

			return primitive with { Name = declaration.Name, Kind = LayoutKind.Enum };
		}

		if (declaration.Kind == TypeKind.Interface) return ReferenceLayout(declaration.Name);

		bool isClass = declaration.IsClass;
		string attributes = declaration.Attributes;
		bool explicitLayout = attributes.Contains("LayoutKind.Explicit", StringComparison.Ordinal);
		bool autoAttribute = attributes.Contains("LayoutKind.Auto", StringComparison.Ordinal);
		int pack = AttributeNumber(attributes, "Pack") is > 0 and var packed ? packed : 8;
		int declaredSize = AttributeNumber(attributes, "Size");
		string? inlineArray = InlineArrayLength(attributes);

		(FieldDeclaration Field, TypeLayout Layout)[] resolved = new (FieldDeclaration, TypeLayout)[declaration.Fields.Count];
		bool hasReferences = false;
		bool exact = true;

		for (int i = 0; i < resolved.Length; i++) {
			FieldDeclaration field = declaration.Fields[i];
			TypeLayout layout = field.IsEvent ? ReferenceLayout(field.TypeText) : StoredAs(await ResolveTypeAsync(field.TypeText, field.TypeOffset, context).ConfigureAwait(false));

			if (field.FixedCount is { } fixedCount) layout = int.TryParse(fixedCount, out int count) ? layout with { Size = layout.Size * count } : layout with { Exact = false, Note = $"fixed buffer length '{fixedCount}' not a literal" };

			resolved[i] = (field, layout);
			hasReferences |= layout.HasReferences;
			exact &= layout.Exact;
		}

		if (inlineArray is not null && resolved.Length >= 1) {
			bool literal = int.TryParse(inlineArray, out int count);
			TypeLayout element = resolved[0].Layout;
			int total = element.Size * (literal ? count : 1);

			return new TypeLayout {
				Name = declaration.Name, Kind = LayoutKind.Struct, Mode = LayoutMode.InlineArray, Size = AlignUp(total, element.Align), Align = element.Align,
				HasReferences = element.HasReferences, Exact = element.Exact && literal,
				Fields = [new FieldLayout($"{resolved[0].Field.Name}[{inlineArray}]", resolved[0].Field.TypeText, 0, total, element.Align, element.Kind == LayoutKind.Reference, 0, element.Exact, resolved[0].Field.NameOffset)],
				Note = literal ? null : $"length '{inlineArray}' is not a literal",
			};
		}

		int baseSize = 0;
		int baseAlign = 1;
		string? baseClass = null;
		bool baseHasReferences = false;

		if (isClass && declaration.BaseList.Count > 0) {
			TypeLayout baseLayout = await ResolveTypeAsync(declaration.BaseList[0].Text, declaration.BaseList[0].Offset, context).ConfigureAwait(false);

			if (baseLayout.Kind == LayoutKind.Class) {
				baseSize = baseLayout.Size;
				baseAlign = baseLayout.Align;
				baseClass = baseLayout.Name;
				baseHasReferences = baseLayout.HasReferences;
			}
		}

		LayoutMode mode = explicitLayout ? LayoutMode.Explicit : isClass || autoAttribute || hasReferences ? LayoutMode.Auto : LayoutMode.Sequential;
		int limit = mode == LayoutMode.Sequential ? pack : 8;

		(FieldDeclaration Field, TypeLayout Layout)[] order = resolved;

		if (mode == LayoutMode.Auto) {
			order = [.. resolved];
			StableSort(order, static (left, right) => {
				int rank = (left.Layout.Kind == LayoutKind.Reference ? 0 : 1) - (right.Layout.Kind == LayoutKind.Reference ? 0 : 1);

				if (rank != 0) return rank;

				int align = right.Layout.Align - left.Layout.Align;

				return align != 0 ? align : right.Layout.Size - left.Layout.Size;
			});
		}

		FieldLayout[] fields = new FieldLayout[order.Length];
		int offset = baseSize;
		int maximumAlign = Math.Max(1, baseAlign);
		int padding = 0;

		for (int i = 0; i < order.Length; i++) {
			(FieldDeclaration field, TypeLayout layout) = order[i];
			int align = Math.Min(layout.Align, limit);
			maximumAlign = Math.Max(maximumAlign, align);
			int at = explicitLayout ? field.FieldOffset ?? 0 : AlignUp(offset, align);
			int pad = explicitLayout ? 0 : at - offset;
			padding += pad;
			fields[i] = new FieldLayout(field.Name, field.TypeText, at, layout.Size, align, layout.Kind == LayoutKind.Reference, pad, layout.Exact, field.NameOffset);
			offset = explicitLayout ? Math.Max(offset, at + layout.Size) : at + layout.Size;
		}

		if (isClass) {
			int heap = Math.Max(MinimumObject, AlignUp(ObjectHeader + offset, Pointer));

			return new TypeLayout {
				Name = declaration.Name, Kind = LayoutKind.Class, Mode = mode, Size = offset, Align = maximumAlign, HasReferences = hasReferences || baseHasReferences, Exact = exact,
				Fields = fields, Padding = padding, TailPadding = heap - ObjectHeader - offset, HeapSize = heap, BaseSize = baseSize, BaseName = baseClass,
			};
		}

		int size = fields.Length > 0 ? AlignUp(offset, maximumAlign) : 1;

		if (declaredSize > size) size = declaredSize;

		int tailPadding = size - offset;
		Reorder? reorder = null;

		if (mode == LayoutMode.Sequential && padding + tailPadding > 0) {
			(FieldDeclaration Field, TypeLayout Layout)[] sorted = [.. resolved];
			StableSort(sorted, (left, right) => {
				int align = Math.Min(right.Layout.Align, limit) - Math.Min(left.Layout.Align, limit);

				return align != 0 ? align : right.Layout.Size - left.Layout.Size;
			});

			ReorderField[] reordered = new ReorderField[sorted.Length];
			int cursor = 0;

			for (int i = 0; i < sorted.Length; i++) {
				cursor = AlignUp(cursor, Math.Min(sorted[i].Layout.Align, limit));
				reordered[i] = new ReorderField(sorted[i].Field.Name, sorted[i].Field.TypeText, cursor, sorted[i].Layout.Size);
				cursor += sorted[i].Layout.Size;
			}

			int compact = AlignUp(cursor, maximumAlign);

			if (compact < size) reorder = new Reorder(compact, size - compact, reordered);
		}

		return new TypeLayout {
			Name = declaration.Name, Kind = LayoutKind.Struct, Mode = mode, Size = size, Align = maximumAlign, HasReferences = hasReferences, Exact = exact,
			Fields = fields, Padding = padding, TailPadding = Math.Max(0, tailPadding), Reorder = reorder,
		};
	}

	private static void StableSort<T>(T[] items, Comparison<T> comparison) {
		for (int i = 1; i < items.Length; i++) {
			T item = items[i];
			int j = i - 1;

			while (j >= 0 && comparison(items[j], item) > 0) {
				items[j + 1] = items[j];
				j--;
			}

			items[j + 1] = item;
		}
	}

	private static int AttributeNumber(string attributes, string name) {
		int index = 0;

		while ((index = attributes.IndexOf(name, index, StringComparison.Ordinal)) >= 0) {
			bool boundary = index == 0 || !(char.IsAsciiLetterOrDigit(attributes[index - 1]) || attributes[index - 1] == '_');
			int i = index + name.Length;
			index = i;

			if (!boundary) continue;

			while (i < attributes.Length && char.IsWhiteSpace(attributes[i])) i++;

			if (i >= attributes.Length || attributes[i] != '=') continue;

			i++;

			while (i < attributes.Length && char.IsWhiteSpace(attributes[i])) i++;

			int start = i;

			while (i < attributes.Length && char.IsAsciiDigit(attributes[i])) i++;

			if (i > start) return int.Parse(attributes.AsSpan(start, i - start));
		}

		return 0;
	}

	private static string? InlineArrayLength(string attributes) {
		int index = attributes.IndexOf("InlineArray", StringComparison.Ordinal);

		if (index < 0) return null;

		int open = attributes.IndexOf('(', index + 11);

		if (open < 0 || attributes.AsSpan(index + 11, open - index - 11).Trim().Length > 0) return null;

		int close = attributes.IndexOf(')', open);

		return close < 0 ? null : attributes.AsSpan(open + 1, close - open - 1).Trim().ToString();
	}

	private static string Normalize(string typeText) {
		ReadOnlySpan<char> trimmed = typeText.AsSpan().Trim();

		if (!trimmed.ContainsAny(" \t\r\n")) return trimmed.Length == typeText.Length ? typeText : trimmed.ToString();

		Span<char> buffer = trimmed.Length <= 256 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
		int written = 0;
		bool space = false;

		foreach (char c in trimmed) {
			if (char.IsWhiteSpace(c)) {
				space = true;
				continue;
			}

			if (space && written > 0) buffer[written++] = ' ';

			space = false;
			buffer[written++] = c;
		}

		return new string(buffer[..written]);
	}

	private async Task<TypeLayout> ResolveTypeAsync(string typeText, int offset, Context context) {
		string text = Normalize(typeText);

		if (text.StartsWith("global::", StringComparison.Ordinal)) text = text[8..];

		bool nullable = text.EndsWith('?');

		if (nullable) text = text[..^1].TrimEnd();

		if (text.EndsWith('*')) return ReferenceLayout(text) with { Kind = LayoutKind.Primitive, HasReferences = false };

		if (text.EndsWith(']') || (text.StartsWith("delegate", StringComparison.Ordinal) && (text.Length == 8 || !char.IsAsciiLetterOrDigit(text[8])))) return ReferenceLayout(text);

		if (text.StartsWith('(')) return await TupleLayoutAsync(text, offset, context).ConfigureAwait(false);

		TypeLayout? inner = context.Substitutions.GetValueOrDefault(text);

		if (inner is null && (char.IsAsciiLetterLower(text[0]) || text.StartsWith("System.", StringComparison.Ordinal))) inner = Primitives.GetValueOrDefault(text) ?? (ReferenceNames.Contains(text) ? ReferenceLayout(text) : null);

		inner ??= await ResolveNamedAsync(text, offset, context).ConfigureAwait(false);

		if (nullable && inner.Kind is LayoutKind.Struct or LayoutKind.Primitive or LayoutKind.Enum) {
			int align = inner.Align;
			int size = AlignUp(AlignUp(1, align) + inner.Size, align);

			return inner with { Name = $"{inner.Name}?", Kind = LayoutKind.Struct, Size = size, Fields = [], Padding = size - 1 - inner.Size, TailPadding = 0, Mode = LayoutMode.Sequential };
		}

		return inner;
	}

	private async Task<TypeLayout> TupleLayoutAsync(string text, int offset, Context context) {
		List<TypeLayout> parts = [];

		foreach (TextPart part in CSharpSource.SplitTopLevel(text.AsSpan(1, text.Length - 2), ',')) {
			string piece = StripTrailingName(part.Text);
			parts.Add(StoredAs(await ResolveTypeAsync(piece, offset + 1 + part.Offset, context).ConfigureAwait(false)));
		}

		bool hasReferences = parts.Exists(static part => part.HasReferences);

		TypeLayout[] order = [.. parts];

		if (hasReferences) StableSort(order, static (left, right) => right.Align - left.Align);

		int cursor = 0;
		int maximumAlign = 1;

		foreach (TypeLayout part in order) {
			maximumAlign = Math.Max(maximumAlign, part.Align);
			cursor = AlignUp(cursor, part.Align) + part.Size;
		}

		return new TypeLayout { Name = text, Kind = LayoutKind.Struct, Size = AlignUp(cursor, maximumAlign), Align = maximumAlign, HasReferences = hasReferences, Exact = parts.TrueForAll(static part => part.Exact), Mode = hasReferences ? LayoutMode.Auto : LayoutMode.Sequential };
	}

	private static string StripTrailingName(string piece) {
		int end = piece.Length;
		int i = end;

		while (i > 0 && (char.IsAsciiLetterOrDigit(piece[i - 1]) || piece[i - 1] == '_')) i--;

		if (i == end || i == 0 || !char.IsWhiteSpace(piece[i - 1]) || char.IsAsciiDigit(piece[i])) return piece;

		return piece[..i].TrimEnd();
	}

	private Task<DefinitionTarget?> Locate(string path, DocumentInfo info, string name, int offset) {
		string key = string.Concat(path, "|", name);

		return _targets.GetOrAdd(key, _ => LocateAsync());

		async Task<DefinitionTarget?> LocateAsync() {
			if (lookup is null) return null;

			int lastDot = name.LastIndexOf('.');
			int at = offset + (lastDot >= 0 ? lastDot + 1 : 0);
			int line = info.LineOf(at);
			DefinitionTarget? hit;

			try {
				hit = await lookup(path, line + 1, at - info.LineStarts[line] + 1).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException or TimeoutException) {
				return null;
			}

			if (hit is { } target) _targetDocuments[key] = target.Path;

			return hit;
		}
	}

	private async Task<TypeLayout> ResolveNamedAsync(string text, int offset, Context context) {
		if (context.Depth > MaximumDepth) return Unknown(text, "nesting too deep");

		(string name, List<TextPart> arguments) = CSharpSource.SplitGenericArguments(text);

		if (context.Path is not { } path || Document(path) is not { } source) return Unknown(text, "no document to resolve against");

		if (await Locate(path, source, name, offset).ConfigureAwait(false) is not { } found) return Unknown(text, "type not resolved");

		string simple = name[(name.LastIndexOf('.') + 1)..];
		bool fromMetadata = !inWorkspace(found.Path);

		if (fromMetadata && Primitives.TryGetValue(simple, out TypeLayout? known)) return known;

		if (Document(found.Path) is not { } info) return Unknown(text, "definition unavailable");

		int nameOffset = info.OffsetOf(found.Line - 1, found.Column - 1);
		int lineStart = info.LineStarts[info.LineOf(nameOffset)];
		int lineBreak = info.Blanked.IndexOf('\n', nameOffset);
		string line = info.Blanked[lineStart..(lineBreak < 0 ? info.Blanked.Length : lineBreak)];

		if (AliasTarget(line) is { } aliased) return await ResolveTypeAsync(aliased.Text, lineStart + aliased.Offset, new Context(found.Path, info.Blanked, [], context.Depth + 1, context.Visiting)).ConfigureAwait(false);

		int start = AttributeStart(info.Blanked, lineStart);

		if (CSharpSource.ParseTypeDeclaration(info.Blanked, start) is not { } declaration) return Unknown(text, "declaration not parsed");

		if (declaration.Kind == TypeKind.Interface) return ReferenceLayout(declaration.Name);

		if (fromMetadata && Primitives.TryGetValue(declaration.Name, out TypeLayout? framework)) return framework;

		if (fromMetadata && declaration.IsClass) return ExternalClass(declaration.Name, declaration.Name == "Object");

		if (fromMetadata && declaration.IsStruct) return Unknown(declaration.Name, $"{declaration.Name} comes from a referenced assembly, whose private fields are not visible, so its size here is a guess.");

		string key = $"{found.Path}#{start}<{string.Join(',', arguments.Select(static argument => argument.Text))}>";

		if (context.Visiting.Contains(key)) return declaration.IsStruct ? Unknown(declaration.Name, "recursive struct") : ExternalClass(declaration.Name) with { Exact = true };

		if (_layouts.TryGetValue(key, out Task<TypeLayout>? existing)) return await existing.ConfigureAwait(false);

		Dictionary<string, TypeLayout> substitutions = [];

		for (int i = 0; i < declaration.TypeParameters.Count; i++) substitutions[declaration.TypeParameters[i]] = i < arguments.Count ? StoredAs(await ResolveTypeAsync(arguments[i].Text, offset + arguments[i].Offset, context).ConfigureAwait(false)) : Unknown(declaration.TypeParameters[i], "open generic");

		HashSet<string> visiting = [.. context.Visiting, key];
		Task<TypeLayout> pending = _layouts.GetOrAdd(key, _ => LayoutOfDeclarationAsync(found.Path, info.Blanked, declaration, substitutions, context.Depth + 1, visiting));

		return await pending.ConfigureAwait(false);
	}

	private static TextPart? AliasTarget(string line) {
		ReadOnlySpan<char> span = line;
		int i = Skip(span, 0);

		if (Word(span, ref i, "global")) i = Skip(span, i);

		if (!Word(span, ref i, "using")) return null;

		i = Skip(span, i);

		if (Word(span, ref i, "unsafe")) i = Skip(span, i);

		if (i < span.Length && span[i] == '@') i++;

		int nameStart = i;

		while (i < span.Length && (char.IsAsciiLetterOrDigit(span[i]) || span[i] == '_')) i++;

		if (i == nameStart || char.IsAsciiDigit(span[nameStart])) return null;

		i = Skip(span, i);

		if (i >= span.Length || span[i] != '=') return null;

		i = Skip(span, i + 1);

		int semicolon = span[i..].IndexOf(';');
		ReadOnlySpan<char> target = (semicolon < 0 ? span[i..] : span.Slice(i, semicolon)).Trim();

		return target.IsEmpty ? null : new TextPart(target.ToString(), i);

		static int Skip(ReadOnlySpan<char> span, int i) {
			while (i < span.Length && span[i] is ' ' or '\t') i++;

			return i;
		}

		static bool Word(ReadOnlySpan<char> span, ref int i, string word) {
			if (!span[i..].StartsWith(word, StringComparison.Ordinal)) return false;

			int end = i + word.Length;

			if (end >= span.Length || span[end] is not (' ' or '\t')) return false;

			i = end;
			return true;
		}
	}

	private static int AttributeStart(string blanked, int lineStart) {
		int start = lineStart;

		while (true) {
			int previousEnd = start - 1;

			if (previousEnd <= 0) return start;

			int previousStart = blanked.LastIndexOf('\n', previousEnd - 1) + 1;
			ReadOnlySpan<char> line = blanked.AsSpan(previousStart, previousEnd - previousStart).Trim();

			if (line.StartsWith("[") && line.EndsWith("]")) {
				start = previousStart;
				continue;
			}

			if (!line.IsEmpty) return start;

			if (previousStart == 0) return lineStart;

			int aboveStart = blanked.LastIndexOf('\n', previousStart - 2) + 1;
			ReadOnlySpan<char> above = blanked.AsSpan(aboveStart, Math.Max(0, previousStart - 1 - aboveStart)).Trim();

			if (!(above.StartsWith("[") && above.EndsWith("]"))) return lineStart;

			start = previousStart;
		}
	}
}
