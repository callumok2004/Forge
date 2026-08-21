namespace Forge.Text;

public sealed class TextSnapshot
{
	private readonly Piece[] _pieces;
	private readonly ChunkView[] _chunks;

	private readonly int[] _pieceStarts;

	private readonly int[] _pieceLineFeeds;

	internal TextSnapshot(Piece[] pieces, ChunkView[] chunks, int version, LineEnding lineEnding) {
		_pieces = pieces;
		_chunks = chunks;
		Version = version;
		LineEnding = lineEnding;

		_pieceStarts = new int[pieces.Length + 1];
		_pieceLineFeeds = new int[pieces.Length + 1];

		int offset = 0;
		int lineFeeds = 0;
		for (int i = 0; i < pieces.Length; i++) {
			_pieceStarts[i] = offset;
			_pieceLineFeeds[i] = lineFeeds;
			offset += pieces[i].Length;
			lineFeeds += pieces[i].LineFeeds;
		}

		_pieceStarts[pieces.Length] = offset;
		_pieceLineFeeds[pieces.Length] = lineFeeds;

		Length = offset;
		LineCount = lineFeeds + 1;
	}

	public int Version { get; }

	public int Length { get; }

	public int LineCount { get; }

	public LineEnding LineEnding { get; }

	public char this[int offset] {
		get {
			int index = FindPieceByOffset(offset);
			Piece piece = _pieces[index];
			return _chunks[piece.Chunk].Slice(piece.Start + (offset - _pieceStarts[index]), 1)[0];
		}
	}

	#region Lookup

	private int FindPieceByOffset(int offset) {
		int lo = 0;
		int hi = _pieces.Length;
		while (lo < hi) {
			int mid = (int)((uint)(lo + hi) >> 1);
			if (_pieceStarts[mid] <= offset) lo = mid + 1;
			else hi = mid;
		}

		return lo - 1;
	}

	private int FindPieceByLineFeed(int lineFeedIndex) {
		int lo = 0;
		int hi = _pieces.Length;
		while (lo < hi) {
			int mid = (int)((uint)(lo + hi) >> 1);
			if (_pieceLineFeeds[mid] <= lineFeedIndex) lo = mid + 1;
			else hi = mid;
		}

		return lo - 1;
	}

	private int FindLineFeedOffset(int index) {
		if (index < 0 || index >= LineCount - 1) return -1;

		int pieceIndex = FindPieceByLineFeed(index);
		Piece piece = _pieces[pieceIndex];
		ChunkView chunk = _chunks[piece.Chunk];

		int local = index - _pieceLineFeeds[pieceIndex];
		int first = chunk.CountLineStartsUpTo(piece.Start);

		return _pieceStarts[pieceIndex] + (chunk.LineStartAt(first + local) - piece.Start) - 1;
	}

	public int GetLineStartOffset(int line) {
		if (line <= 0) return 0;

		return line >= LineCount ? Length : FindLineFeedOffset(line - 1) + 1;
	}

	public int GetLineAt(int offset) {
		if (offset <= 0 || _pieces.Length == 0) return 0;

		if (offset > Length) offset = Length;

		int pieceIndex = FindPieceByOffset(offset);
		if (pieceIndex >= _pieces.Length) return LineCount - 1;

		Piece piece = _pieces[pieceIndex];
		ChunkView chunk = _chunks[piece.Chunk];
		int within = offset - _pieceStarts[pieceIndex];

		int inPiece = chunk.CountLineStartsUpTo(piece.Start + within) - chunk.CountLineStartsUpTo(piece.Start);
		return _pieceLineFeeds[pieceIndex] + inPiece;
	}

	public int GetLineLength(int line) {
		return GetLineSpan(line).Length;
	}

	public TextSpan GetLineSpan(int line) {
		int start = GetLineStartOffset(line);
		if (line >= LineCount - 1) return TextSpan.FromBounds(start, Length);

		int end = FindLineFeedOffset(line);

		if (end > start && this[end - 1] == '\r') end--;

		return TextSpan.FromBounds(start, end);
	}

	public TextSpan GetLineSpanWithTerminator(int line) {
		int start = GetLineStartOffset(line);
		int end = line >= LineCount - 1 ? Length : FindLineFeedOffset(line) + 1;
		return TextSpan.FromBounds(start, end);
	}

	public TextPosition GetPosition(int offset) {
		int line = GetLineAt(offset);
		return new TextPosition(line, offset - GetLineStartOffset(line));
	}

	public int GetOffset(TextPosition position) {
		if (position.Line >= LineCount) return Length;

		TextSpan line = GetLineSpan(position.Line);
		return line.Start + Math.Clamp(position.Column, 0, line.Length);
	}

	#endregion

	#region Reading

	public void CopyTo(int offset, int count, Span<char> destination) {
		if (count <= 0) return;

		ArgumentOutOfRangeException.ThrowIfGreaterThan(count, destination.Length);

		int index = FindPieceByOffset(offset);
		int within = offset - _pieceStarts[index];
		int written = 0;

		while (index < _pieces.Length && written < count) {
			Piece piece = _pieces[index];
			int available = piece.Length - within;
			if (available > 0) {
				int take = Math.Min(available, count - written);
				_chunks[piece.Chunk].Slice(piece.Start + within, take).CopyTo(destination[written..]);
				written += take;
			}

			within = 0;
			index++;
		}
	}

	public string GetText(int offset, int count) {
		if (count <= 0) return string.Empty;

		return string.Create(count, (Snapshot: this, Offset: offset), static (span, state) =>
			state.Snapshot.CopyTo(state.Offset, span.Length, span));
	}

	public string GetText(TextSpan span) => GetText(span.Start, span.Length);

	public string GetText() => GetText(0, Length);

	public int CopyLineTo(int line, Span<char> destination) {
		TextSpan span = GetLineSpan(line);
		int count = Math.Min(span.Length, destination.Length);
		CopyTo(span.Start, count, destination);
		return count;
	}

	#endregion
}
