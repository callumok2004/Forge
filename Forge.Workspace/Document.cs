using Forge.Common.Editor;
using Forge.Text;

namespace Forge.Workspace;

public sealed class Document : IDocument
{
	public string? GetPath() => Path;

	public string GetName() => Name;

	public TextBuffer GetBuffer() => Buffer;

	public bool IsDirty() => Buffer.Version != _savedVersion;

	private int _savedVersion;

	private Document(string? path, string name, TextBuffer buffer) {
		Path = path;
		Name = name;
		Buffer = buffer;
		_savedVersion = buffer.Version;
	}

	public string? Path { get; private set; }

	public string Name { get; private set; }

	public TextBuffer Buffer { get; }

	public static Document Open(string path) {
		TextBuffer buffer = TextLoader.Load(path);
		return new Document(path, System.IO.Path.GetFileName(path), buffer);
	}

	public static Document Untitled(string name, string text) => new(null, name, new TextBuffer(text));

	public bool Save() {
		if (Path is null) return false;

		return SaveAs(Path);
	}

	public bool SaveAs(string path) {
		string temporary = path + ".forge-tmp";

		try {
			using (StreamWriter writer = new(temporary, append: false, System.Text.Encoding.UTF8)) {
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
		_savedVersion = Buffer.Version;
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
