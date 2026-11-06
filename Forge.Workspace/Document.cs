using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Workspace;

public sealed class Document : IDocument
{
	public string? GetPath() => Path;

	public string GetName() => Name;

	public TextBuffer GetBuffer() => Buffer;

	public bool IsDirty() => Buffer.State != _savedState;

	public void MarkSaved() => _savedState = Buffer.State;

	private int _savedState;

	private Document(string? path, string name, TextBuffer buffer) {
		Path = path;
		Name = name;
		Buffer = buffer;
		_savedState = buffer.State;
	}

	public string? Path { get; private set; }

	public string Name { get; private set; }

	public TextBuffer Buffer { get; }

	public static Document Open(string path) {
		TextBuffer buffer = TextLoader.Load(path);
		return new Document(path, System.IO.Path.GetFileName(path), buffer) { DiskStamp = Stamp(path) };
	}

	public static Document Untitled(string name, string text) => new(null, name, new TextBuffer(text));

	public static Document Virtual(string uri, string name, string text) => new(null, name, new TextBuffer(text)) { VirtualUri = uri };

	public string? VirtualUri { get; private init; }

	public DateTime DiskStamp { get; private set; }

	private static DateTime Stamp(string path) {
		try {
			return File.GetLastWriteTimeUtc(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return default;
		}
	}

	public bool ReloadIfChangedOnDisk() {
		if (Path is not { } path || IsDirty()) return false;

		DateTime stamp = Stamp(path);

		if (stamp == default || stamp == DiskStamp) return false;

		string text;

		try {
			text = TextLoader.Load(path).GetText();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return false;
		}

		DiskStamp = stamp;
		string current = Buffer.GetText();

		if (string.Equals(current, text, StringComparison.Ordinal)) return false;

		Buffer.ReplaceChanged(current, text);
		_savedState = Buffer.State;
		return true;
	}

	public string? DiffOriginalUri { get; set; }

	public string? DiffOriginalText { get; set; }

	public Func<string, bool>? SaveHandler { get; set; }

	public bool Save() {
		if (Path is not null) return SaveAs(Path);

		if (SaveHandler is not { } handler || !handler(Buffer.GetText())) return false;

		_savedState = Buffer.State;
		return true;
	}

	public bool SaveAs(string path) {
		string temporary = path + ".forge-tmp";

		try {
			using (StreamWriter writer = new(temporary, append: false, Buffer.Encoding)) {
				WriteTo(writer);
			}

			File.Move(temporary, path, overwrite: true);
		}
		catch (IOException) {
			TryDelete(temporary);
			return false;
		}
		catch (UnauthorizedAccessException) {
			TryDelete(temporary);
			return false;
		}

		Path = path;
		Name = System.IO.Path.GetFileName(path);
		_savedState = Buffer.State;
		DiskStamp = Stamp(path);
		return true;
	}

	private void WriteTo(StreamWriter writer) {
		const int chunkSize = 64 * 1024;

		TextSnapshot snapshot = Buffer.CreateSnapshot();
		char[] chunk = new char[chunkSize];

		for (int offset = 0; offset < snapshot.Length; offset += chunkSize) {
			int count = Math.Min(chunkSize, snapshot.Length - offset);
			snapshot.CopyTo(offset, count, chunk);
			writer.Write(chunk, 0, count);
		}
	}

	private static void TryDelete(string path) {
		try {
			File.Delete(path);
		}
		catch (IOException) {
		}
		catch (UnauthorizedAccessException) {
		}
	}
}
