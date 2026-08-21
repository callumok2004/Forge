using System.Runtime.CompilerServices;

namespace Forge.Text;

internal sealed class TextChunk
{
	private int[] _lineStarts;
	private int _lineStartCount;

	private char[] _buffer;
	private int _length;

	public TextChunk(int capacity) {
		_buffer = capacity > 0 ? new char[capacity] : [];
		_length = 0;
		_lineStarts = new int[Math.Max(4, capacity / 32)];
		_lineStarts[0] = 0;
		_lineStartCount = 1;
	}

	public TextChunk(char[] buffer, int length) {
		_buffer = buffer;
		_length = length;

		_lineStarts = new int[Math.Max(4, length / 32)];
		_lineStarts[0] = 0;
		_lineStartCount = 1;
		IndexLineStarts(0, length);
	}

	public int Length => _length;

	public ReadOnlySpan<char> Span => _buffer.AsSpan(0, _length);

	public ReadOnlySpan<char> Slice(int start, int length) => _buffer.AsSpan(start, length);

	public int Append(ReadOnlySpan<char> text) {
		int start = _length;
		int required = _length + text.Length;
		if (required > _buffer.Length) {
			int capacity = Math.Max(_buffer.Length == 0 ? 1024 : _buffer.Length * 2, required);
			Array.Resize(ref _buffer, capacity);
		}

		text.CopyTo(_buffer.AsSpan(start));
		_length = required;
		IndexLineStarts(start, required);
		return start;
	}

	public bool IsAtEnd(int end) => end == _length;

	private void IndexLineStarts(int from, int to) {
		ReadOnlySpan<char> span = _buffer.AsSpan(from, to - from);
		int searchBase = from;
		while (true) {
			int index = span.IndexOf('\n');
			if (index < 0) break;

			AddLineStart(searchBase + index + 1);
			searchBase += index + 1;
			span = span[(index + 1)..];
		}
	}

	private void AddLineStart(int offset) {
		if (_lineStartCount == _lineStarts.Length) Array.Resize(ref _lineStarts, _lineStarts.Length * 2);

		_lineStarts[_lineStartCount++] = offset;
	}

	public int CountLineStartsUpTo(int offset) {
		int lo = 0;
		int hi = _lineStartCount;
		while (lo < hi) {
			int mid = (int)((uint)(lo + hi) >> 1);
			if (_lineStarts[mid] <= offset) lo = mid + 1;
			else hi = mid;
		}

		return lo;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int CountLineFeeds(int start, int end) => CountLineStartsUpTo(end) - CountLineStartsUpTo(start);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int LineStartAt(int index) => _lineStarts[index];

	public ChunkView CreateView() => new(_buffer, _lineStarts, _lineStartCount);
}

internal readonly struct ChunkView(char[] buffer, int[] lineStarts, int lineStartCount)
{
	private readonly char[] _buffer = buffer;
	private readonly int[] _lineStarts = lineStarts;
	private readonly int _lineStartCount = lineStartCount;

	public ReadOnlySpan<char> Slice(int start, int length) => _buffer.AsSpan(start, length);

	public int CountLineStartsUpTo(int offset) {
		int lo = 0;
		int hi = _lineStartCount;
		while (lo < hi) {
			int mid = (int)((uint)(lo + hi) >> 1);
			if (_lineStarts[mid] <= offset) lo = mid + 1;
			else hi = mid;
		}

		return lo;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int LineStartAt(int index) => _lineStarts[index];
}
