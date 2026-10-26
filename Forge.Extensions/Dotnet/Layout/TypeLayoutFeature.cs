using System.Globalization;
using System.Text;

using Forge.Common.Editor;

namespace Forge.Extensions.Dotnet.Layout;

public sealed class TypeLayoutFeature(LayoutEngine engine, DotnetSettings settings, Action changed)
{
	public const int LargeStruct = 16;
	public const int EditDelayMilliseconds = 120;

	private const string Dot = "  ·  ";

	private readonly Lock _gate = new();

	private string? _path;
	private int _version = -1;
	private List<DeclarationLayout>? _entries;
	private Dictionary<int, LineHint>? _hints;
	private CancellationTokenSource? _running;
	private (string Path, int Version) _pending;

	public int Version { get; private set; }

	public LayoutEngine Engine => engine;

	public void Refresh() {
		engine.Invalidate();

		lock (_gate) {
			_version = -1;
			_pending = default;
		}

		Version++;
		changed();
	}

	public IReadOnlyDictionary<int, LineHint>? Hints(string path, int version) {
		if (!settings.LayoutEnabled) return null;

		lock (_gate) {
			if (_version == version && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return _hints;

			if (_pending == (path, version)) return null;

			bool edit = _version >= 0 && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);

			_running?.Cancel();
			_running = new CancellationTokenSource();
			_pending = (path, version);
			CancellationToken cancellation = _running.Token;

			if (edit) engine.Forget(path);

			_ = ComputeAsync(path, version, edit ? EditDelayMilliseconds : 0, cancellation);
		}

		return null;
	}

	private async Task ComputeAsync(string path, int version, int delay, CancellationToken cancellation) {
		try {
			if (delay > 0) await Task.Delay(delay, cancellation).ConfigureAwait(false);

			List<DeclarationLayout> found = await Task.Run(() => engine.LayoutsInAsync(path, cancellation), cancellation).ConfigureAwait(false);

			if (cancellation.IsCancellationRequested || engine.Document(path) is not { } info) return;

			Dictionary<int, LineHint> hints = BuildHints(found, info, settings.LayoutFieldOffsets);

			lock (_gate) {
				if (_pending != (path, version)) return;

				_path = path;
				_version = version;
				_entries = found;
				_hints = hints;
			}

			Version++;
			changed();
		}
		catch (OperationCanceledException) {
		}
	}

	public static Dictionary<int, LineHint> BuildHints(List<DeclarationLayout> entries, LayoutEngine.DocumentInfo info, bool showFields) {
		Dictionary<int, LineHint> hints = [];

		foreach ((TypeDeclaration declaration, _, TypeLayout layout) in entries) {
			hints.TryAdd(info.LineOf(declaration.NameOffset), new LineHint(Summary(layout), false));

			if (!showFields || layout.Kind == LayoutKind.Enum) continue;

			foreach (FieldLayout field in layout.Fields) {
				if (field.NameOffset <= 0) continue;

				hints.TryAdd(info.LineOf(field.NameOffset), new LineHint(FieldHint(field), field.PadBefore > 0));
			}
		}

		return hints;
	}

	public static string Summary(TypeLayout layout) {
		if (layout.Kind == LayoutKind.Enum) return $"{layout.Size} B";

		string approximate = layout.Exact ? string.Empty : "~";
		StringBuilder builder = new(layout.Kind == LayoutKind.Class ? $"{approximate}{layout.HeapSize} B/object" : $"{approximate}{layout.Size} B");
		int waste = layout.Padding + layout.TailPadding;

		if (waste > 0) builder.Append(Dot).Append(waste).Append(" B wasted");

		if (layout.Reorder is { } reorder) builder.Append(Dot).Append(reorder.Size).Append(" B if reordered");

		return builder.ToString();
	}

	public static string FieldHint(FieldLayout field) {
		string size = $"{(field.Exact ? string.Empty : "~")}{field.Size} B at byte {field.Offset}";

		return field.PadBefore > 0 ? $"{size}{Dot}{field.PadBefore} B hole before" : size;
	}

	private static string ByteRange(int offset, int size) => size <= 1 ? offset.ToString(CultureInfo.InvariantCulture) : $"{offset}–{offset + size - 1}";

	private static string EscapeCell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

	public static string Table(TypeLayout layout) {
		StringBuilder md = new();

		if (layout.Kind == LayoutKind.Enum) return md.Append("**").Append(layout.Name).Append("** — ").Append(layout.Size).Append(" B\n").ToString();

		string approximate = layout.Exact ? string.Empty : "about ";

		if (layout.Kind == LayoutKind.Class) {
			md.Append($"**{layout.Name}** — {approximate}**{layout.HeapSize} B** per object on the heap\n\n");
			md.Append("16 B of that is the object header and method table pointer");
			md.Append(layout.BaseSize > 0 ? $", and {layout.BaseSize} B comes from `{layout.BaseName}`.\n\n" : ".\n\n");
		}
		else md.Append($"**{layout.Name}** — {approximate}**{layout.Size} B**, aligned to {layout.Align} B\n\n");

		if (layout.Fields.Length > 0) {
			md.Append("| bytes | size | field | type |\n|---|---:|---|---|\n");

			foreach (FieldLayout field in layout.Fields) {
				if (field.PadBefore > 0) md.Append($"| {ByteRange(field.Offset - field.PadBefore, field.PadBefore)} | {field.PadBefore} B | *(padding)* | |\n");

				md.Append($"| {ByteRange(field.Offset, field.Size)} | {(field.Exact ? string.Empty : "~")}{field.Size} B | {field.Name} | `{EscapeCell(field.TypeText)}` |\n");
			}

			if (layout.TailPadding > 0) {
				int end = (layout.Kind == LayoutKind.Class ? layout.HeapSize - LayoutEngine.ObjectHeader : layout.Size) - layout.TailPadding;
				md.Append($"| {ByteRange(end, layout.TailPadding)} | {layout.TailPadding} B | *(padding at the end)* | |\n");
			}

			md.Append("\n**bytes** is where the field sits inside the value, **size** is how many bytes it takes.\n");
		}

		int waste = layout.Padding + layout.TailPadding;

		if (waste > 0) md.Append($"\n{waste} B of that is padding the compiler inserts to keep each field on its natural boundary.\n");

		if (layout.Reorder is { } reorder) {
			md.Append($"\n---\n\n**Reordering saves {reorder.Saved} B** ({layout.Size} B → {reorder.Size} B). Declare them in this order:\n\n");

			foreach (ReorderField field in reorder.Order) md.Append($"1. `{EscapeCell(field.TypeText)} {field.Name};` — bytes {ByteRange(field.Offset, field.Size)}\n");

			md.Append("\nWidest alignment first, narrowest last, so no gaps are left between fields.\n");
		}
		else if (layout.Kind != LayoutKind.Class && layout.Mode == LayoutMode.Sequential && waste > 0) md.Append("\nReordering the fields would not make it any smaller.\n");

		if (layout.Kind != LayoutKind.Class && layout.Size > LargeStruct) md.Append("\nLarger than 16 B, so passing it by value copies every byte at each call. Take it as `in` (read-only) or `ref` to pass an 8-byte reference instead.\n");

		if (layout.Kind != LayoutKind.Class && layout.HasReferences && layout.Mode == LayoutMode.Auto) md.Append("\nIt holds GC references, so the runtime lays it out automatically: the order above is this tool's reconstruction of those rules, not your declaration order, and an individual offset may differ from the real one. The total size is still right.\n");

		if (layout.Mode == LayoutMode.Explicit) md.Append("\nExplicit layout: every offset comes from its `[FieldOffset]`.\n");

		if (!layout.Exact) md.Append("\n`~` marks a size that could not be measured exactly, such as a struct from a referenced assembly or a length that is not a literal.\n");

		if (layout.Note is { } note) md.Append('\n').Append(note).Append('\n');

		md.Append("\n*x64 CoreCLR layout, computed from source without building.*");

		return md.ToString();
	}

	public async Task<string?> HoverAsync(string path, int line, int column) {
		if (!settings.LayoutEnabled || engine.Document(path) is not { } info) return null;

		int lineIndex = line - 1;

		if ((uint)lineIndex >= (uint)info.LineStarts.Length) return null;

		string text = info.Blanked;
		int at = info.OffsetOf(lineIndex, column - 1);
		int start = at;
		int end = at;

		while (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;

		while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_')) end++;

		if (end <= start || char.IsAsciiDigit(text[start])) return null;

		ReadOnlySpan<char> name = text.AsSpan(start, end - start);

		lock (_gate) {
			if (_entries is { } entries && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) {
				foreach ((TypeDeclaration declaration, _, TypeLayout layout) in entries) {
					if (name.SequenceEqual(declaration.Name) && info.LineOf(declaration.NameOffset) == lineIndex) return Table(layout);
				}
			}
		}

		if (!char.IsAsciiLetterUpper(name[0])) return null;

		try {
			TypeLayout found = await engine.LayoutAtAsync(path, start, name.ToString()).ConfigureAwait(false);

			return found.Kind is LayoutKind.Struct or LayoutKind.Enum || (found.Kind == LayoutKind.Class && found.HeapSize >= 0) ? Table(found) : null;
		}
		catch (Exception exception) when (exception is IOException or InvalidOperationException) {
			return null;
		}
	}
}
