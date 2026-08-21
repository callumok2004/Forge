using System.Buffers;
using System.Text.Unicode;
using System.Text;

namespace Forge.Render;

internal sealed class IconMap
{
	private const int MaxKeyBytes = 512;

	private readonly byte[] _blob;

	private readonly int[] _slots;

	private readonly int _mask;

	private IconMap(byte[] blob, int[] slots) {
		_blob = blob;
		_slots = slots;
		_mask = slots.Length - 1;
	}

	public string DefaultFile { get; private set; } = "file";

	public string DefaultFolder { get; private set; } = "folder";

	public string DefaultFolderExpanded { get; private set; } = "folder-open";

	public long ApproximateBytes => _blob.Length + ((long)_slots.Length * sizeof(int));

	public int EntryCount {
		get {
			int count = 0;

			for (int i = 0; i < _slots.Length; i++) {
				if (_slots[i] != 0) count++;
			}

			return count;
		}
	}

	public int FindFile(string fileName) {
		Span<byte> scratch = stackalloc byte[MaxKeyBytes];

		int line = Find(scratch, "name"u8, fileName);

		if (line >= 0) return line;

		int dot = fileName.IndexOf('.');

		while (dot >= 0 && dot < fileName.Length - 1) {
			line = Find(scratch, "ext"u8, fileName.AsSpan(dot + 1));

			if (line >= 0) return line;

			dot = fileName.IndexOf('.', dot + 1);
		}

		return -1;
	}

	public int FindFolder(string folderName, bool isExpanded) {
		Span<byte> scratch = stackalloc byte[MaxKeyBytes];

		return Find(scratch, isExpanded ? "diropen"u8 : "dir"u8, folderName);
	}

	public string IconName(int lineStart) {
		(int start, int length) = IconAt(lineStart);

		return length == 0 ? string.Empty : Encoding.UTF8.GetString(_blob, start, length);
	}

	public int IconOffset(int lineStart) => IconAt(lineStart).Start;

	#region Lookup

	private int Find(Span<byte> scratch, ReadOnlySpan<byte> section, ReadOnlySpan<char> key) {
		section.CopyTo(scratch);
		scratch[section.Length] = (byte)'\t';

		int prefix = section.Length + 1;

		if (Utf8.FromUtf16(key, scratch[prefix..], out _, out int written) != OperationStatus.Done) return -1;

		ReadOnlySpan<byte> needle = scratch[..(prefix + written)];

		for (uint slot = Hash(needle) & (uint)_mask; ; slot = (slot + 1) & (uint)_mask) {
			int stored = _slots[slot];

			if (stored == 0) return -1;

			if (Matches(stored - 1, needle)) return stored - 1;
		}
	}

	private bool Matches(int start, ReadOnlySpan<byte> needle) =>
		_blob.Length - start > needle.Length &&
		_blob[start + needle.Length] == (byte)'\t' &&
		EqualsIgnoreAsciiCase(_blob.AsSpan(start, needle.Length), needle);

	private static uint Hash(ReadOnlySpan<byte> key) {
		uint hash = 2166136261u;

		for (int i = 0; i < key.Length; i++) hash = (hash ^ Fold(key[i])) * 16777619u;

		return hash;
	}

	private static byte Fold(byte value) =>
		value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + 32) : value;

	private static bool EqualsIgnoreAsciiCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) {
		for (int i = 0; i < left.Length; i++) {
			if (Fold(left[i]) != Fold(right[i])) return false;
		}

		return true;
	}

	private (int Start, int Length) IconAt(int lineStart) {
		int first = FieldEnd(lineStart);
		int second = first < 0 ? -1 : FieldEnd(first + 1);

		if (second < 0) return (0, 0);

		int end = second + 1;

		while (end < _blob.Length && _blob[end] is not ((byte)'\n' or (byte)'\r')) end++;

		return (second + 1, end - second - 1);
	}

	private int FieldEnd(int from) {
		for (int i = from; i < _blob.Length; i++) {
			if (_blob[i] == (byte)'\t') return i;

			if (_blob[i] is (byte)'\n' or (byte)'\r') return -1;
		}

		return -1;
	}

	#endregion

	#region Loading

	public static IconMap Load(string mapPath) {
		byte[] blob = File.ReadAllBytes(mapPath);

		int lines = 1;

		for (int i = 0; i < blob.Length; i++) {
			if (blob[i] == (byte)'\n') lines++;
		}

		int capacity = 16;

		while (capacity < lines * 2) capacity *= 2;

		IconMap map = new(blob, new int[capacity]);

		int start = 0;

		while (start < blob.Length) {
			int end = start;

			while (end < blob.Length && blob[end] != (byte)'\n') end++;

			map.Index(start, end > start && blob[end - 1] == (byte)'\r' ? end - 1 : end);
			start = end + 1;
		}

		return map;
	}

	private void Index(int start, int end) {
		int first = -1;
		int second = -1;

		for (int i = start; i < end; i++) {
			if (_blob[i] != (byte)'\t') continue;

			if (first < 0) first = i;
			else {
				second = i;
				break;
			}
		}

		if (first < 0) return;

		if (second < 0) {
			ReadOnlySpan<byte> section = _blob.AsSpan(start, first - start);
			string value = Encoding.UTF8.GetString(_blob, first + 1, end - first - 1);

			if (section.SequenceEqual("default-file"u8)) DefaultFile = value;
			else if (section.SequenceEqual("default-dir"u8)) DefaultFolder = value;
			else if (section.SequenceEqual("default-diropen"u8)) DefaultFolderExpanded = value;

			return;
		}

		ReadOnlySpan<byte> key = _blob.AsSpan(start, second - start);

		for (uint slot = Hash(key) & (uint)_mask; ; slot = (slot + 1) & (uint)_mask) {
			int stored = _slots[slot];

			if (stored == 0 || Matches(stored - 1, key)) {
				_slots[slot] = start + 1;
				return;
			}
		}
	}

	#endregion
}
