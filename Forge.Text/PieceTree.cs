using System.Runtime.CompilerServices;

namespace Forge.Text;

internal struct Piece
{
	public int Chunk;
	public int Start;
	public int Length;
	public int LineFeeds;
}

internal struct PieceNode
{
	public int Parent;
	public int Left;
	public int Right;
	public bool Red;
	public Piece Piece;

	public int SizeLeft;

	public int LineFeedLeft;
}

internal sealed class PieceTree
{
	private const int Nil = 0;
	private readonly TextChunk[] _chunks;

	private readonly int _addedChunk;

	private PieceNode[] _nodes;
	private int _nodeHighWater;
	private int _freeHead;
	private int _root;

	private int _length;
	private int _lineFeeds;

	public PieceTree(char[] original, int originalLength)
		: this([original], [originalLength]) {
	}

	public PieceTree(char[][] chunks, int[] lengths) {
		_chunks = new TextChunk[chunks.Length + 1];

		for (int i = 0; i < chunks.Length; i++) _chunks[i] = new TextChunk(chunks[i], lengths[i]);

		_addedChunk = chunks.Length;
		_chunks[_addedChunk] = new TextChunk(0);

		_nodes = new PieceNode[16];
		_nodeHighWater = 1;
		_freeHead = Nil;
		_root = Nil;

		int previous = Nil;

		for (int i = 0; i < chunks.Length; i++) {
			if (lengths[i] <= 0) continue;

			Piece piece = MakePiece(i, 0, lengths[i]);

			if (previous == Nil) {
				previous = AllocateNode(in piece);
				_root = previous;
				_nodes[previous].Red = false;
				_length = piece.Length;
				_lineFeeds = piece.LineFeeds;
			}
			else previous = InsertAfter(previous, piece);
		}
	}

	public int Length => _length;

	public int LineCount => _lineFeeds + 1;

	#region Node storage

	private int AllocateNode(in Piece piece) {
		int index;
		if (_freeHead != Nil) {
			index = _freeHead;
			_freeHead = _nodes[index].Parent;
		}
		else {
			if (_nodeHighWater == _nodes.Length) Array.Resize(ref _nodes, _nodes.Length * 2);

			index = _nodeHighWater++;
		}

		ref PieceNode node = ref _nodes[index];
		node.Parent = Nil;
		node.Left = Nil;
		node.Right = Nil;
		node.Red = true;
		node.Piece = piece;
		node.SizeLeft = 0;
		node.LineFeedLeft = 0;
		return index;
	}

	private void FreeNode(int index) {
		_nodes[index] = default;
		_nodes[index].Parent = _freeHead;
		_freeHead = index;
	}

	private Piece MakePiece(int chunk, int start, int length) => new() {
		Chunk = chunk,
		Start = start,
		Length = length,
		LineFeeds = _chunks[chunk].CountLineFeeds(start, start + length),
	};

	#endregion

	#region Navigation

	private int Leftmost(int node) {
		while (node != Nil && _nodes[node].Left != Nil) node = _nodes[node].Left;

		return node;
	}

	private int Rightmost(int node) {
		while (node != Nil && _nodes[node].Right != Nil) node = _nodes[node].Right;

		return node;
	}

	private int Successor(int node) {
		if (_nodes[node].Right != Nil) return Leftmost(_nodes[node].Right);

		int parent = _nodes[node].Parent;
		while (parent != Nil && _nodes[parent].Right == node) {
			node = parent;
			parent = _nodes[parent].Parent;
		}

		return parent;
	}

	private int Predecessor(int node) {
		if (_nodes[node].Left != Nil) return Rightmost(_nodes[node].Left);

		int parent = _nodes[node].Parent;
		while (parent != Nil && _nodes[parent].Left == node) {
			node = parent;
			parent = _nodes[parent].Parent;
		}

		return parent;
	}

	private (int Node, int Remainder) FindNodeAt(int offset) {
		if (_root == Nil) return (Nil, 0);

		if (offset >= _length) {
			int last = Rightmost(_root);
			return (last, _nodes[last].Piece.Length);
		}

		int node = _root;
		while (node != Nil) {
			ref PieceNode current = ref _nodes[node];
			if (offset < current.SizeLeft) node = current.Left;
			else if (offset < current.SizeLeft + current.Piece.Length) return (node, offset - current.SizeLeft);
			else {
				offset -= current.SizeLeft + current.Piece.Length;
				node = current.Right;
			}
		}

		return (Nil, 0);
	}

	#endregion

	#region Line and offset lookup

	private int FindLineFeedOffset(int index) {
		int node = _root;
		int offset = 0;
		while (node != Nil) {
			ref PieceNode current = ref _nodes[node];
			if (current.LineFeedLeft > index) {
				node = current.Left;
				continue;
			}

			index -= current.LineFeedLeft;
			offset += current.SizeLeft;

			if (index < current.Piece.LineFeeds) {
				TextChunk chunk = _chunks[current.Piece.Chunk];
				int first = chunk.CountLineStartsUpTo(current.Piece.Start);
				int lineStart = chunk.LineStartAt(first + index);

				return offset + (lineStart - current.Piece.Start) - 1;
			}

			index -= current.Piece.LineFeeds;
			offset += current.Piece.Length;
			node = current.Right;
		}

		return -1;
	}

	public int GetLineStartOffset(int line) {
		if (line <= 0) return 0;

		if (line > _lineFeeds) return _length;

		return FindLineFeedOffset(line - 1) + 1;
	}

	public int GetLineAt(int offset) {
		if (offset <= 0 || _root == Nil) return 0;

		if (offset > _length) offset = _length;

		int line = 0;
		int node = _root;
		while (node != Nil) {
			ref PieceNode current = ref _nodes[node];
			if (offset < current.SizeLeft) {
				node = current.Left;
				continue;
			}

			offset -= current.SizeLeft;
			line += current.LineFeedLeft;

			if (offset <= current.Piece.Length) {
				TextChunk chunk = _chunks[current.Piece.Chunk];
				return line + chunk.CountLineFeeds(current.Piece.Start, current.Piece.Start + offset);
			}

			offset -= current.Piece.Length;
			line += current.Piece.LineFeeds;
			node = current.Right;
		}

		return line;
	}

	public int GetLineLength(int line) {
		int start = GetLineStartOffset(line);
		if (line >= _lineFeeds) return _length - start;

		int lineFeed = FindLineFeedOffset(line);
		int end = lineFeed;
		if (end > start && CharAt(end - 1) == '\r') end--;

		return end - start;
	}

	#endregion

	#region Reading

	public char CharAt(int offset) {
		(int node, int remainder) = FindNodeAt(offset);
		if (node == Nil) return '\0';

		ref PieceNode current = ref _nodes[node];
		return _chunks[current.Piece.Chunk].Slice(current.Piece.Start + remainder, 1)[0];
	}

	public void CopyTo(int offset, int count, Span<char> destination) {
		if (count <= 0) return;

		(int node, int remainder) = FindNodeAt(offset);
		int written = 0;
		while (node != Nil && written < count) {
			Piece piece = _nodes[node].Piece;
			int available = piece.Length - remainder;
			if (available > 0) {
				int take = Math.Min(available, count - written);
				_chunks[piece.Chunk].Slice(piece.Start + remainder, take).CopyTo(destination[written..]);
				written += take;
			}

			remainder = 0;
			node = Successor(node);
		}
	}

	public string GetText(int offset, int count) {
		if (count <= 0) return string.Empty;

		return string.Create(count, (Tree: this, Offset: offset), static (span, state) =>
			state.Tree.CopyTo(state.Offset, span.Length, span));
	}

	public Piece[] SnapshotPieces() {
		if (_root == Nil) return [];

		Piece[] pieces = new Piece[CountNodes()];
		int index = 0;
		for (int node = Leftmost(_root); node != Nil; node = Successor(node)) pieces[index++] = _nodes[node].Piece;

		return index == pieces.Length ? pieces : pieces[..index];
	}

	internal ChunkView[] CreateChunkViews() {
		ChunkView[] views = new ChunkView[_chunks.Length];
		for (int i = 0; i < _chunks.Length; i++) views[i] = _chunks[i].CreateView();

		return views;
	}

	internal int PieceCount => CountNodes();

	private int CountNodes() {
		int count = 0;
		for (int node = Leftmost(_root); node != Nil; node = Successor(node)) count++;

		return count;
	}

	#endregion

	#region Insert

	public void Insert(int offset, ReadOnlySpan<char> text) {
		if (text.IsEmpty) return;

		if (offset < 0) offset = 0;
		else if (offset > _length) offset = _length;

		if (_root == Nil) {
			Piece piece = AppendToAddedChunk(text);
			_root = AllocateNode(in piece);
			_nodes[_root].Red = false;
			_length += piece.Length;
			_lineFeeds += piece.LineFeeds;
			return;
		}

		(int node, int remainder) = FindNodeAt(offset);

		if (remainder == _nodes[node].Piece.Length) {
			if (TryExtendPiece(node, text)) return;

			InsertAfter(node, AppendToAddedChunk(text));
			return;
		}

		if (remainder == 0) {
			int previous = Predecessor(node);
			if (previous != Nil && TryExtendPiece(previous, text)) return;

			InsertBefore(node, AppendToAddedChunk(text));
			return;
		}

		SplitAndInsert(node, remainder, text);
	}

	private Piece AppendToAddedChunk(ReadOnlySpan<char> text) {
		int start = _chunks[_addedChunk].Append(text);
		return MakePiece(_addedChunk, start, text.Length);
	}

	private bool TryExtendPiece(int node, ReadOnlySpan<char> text) {
		ref PieceNode current = ref _nodes[node];
		if (current.Piece.Chunk != _addedChunk) return false;

		TextChunk chunk = _chunks[_addedChunk];
		if (!chunk.IsAtEnd(current.Piece.Start + current.Piece.Length)) return false;

		int start = chunk.Append(text);
		int lineFeeds = chunk.CountLineFeeds(start, start + text.Length);

		_nodes[node].Piece.Length += text.Length;
		_nodes[node].Piece.LineFeeds += lineFeeds;
		_length += text.Length;
		_lineFeeds += lineFeeds;
		UpdateMetadataUpward(node, text.Length, lineFeeds);
		return true;
	}

	private void SplitAndInsert(int node, int remainder, ReadOnlySpan<char> text) {
		Piece original = _nodes[node].Piece;

		int headLineFeeds = _chunks[original.Chunk]
			.CountLineFeeds(original.Start, original.Start + remainder);

		ResizePiece(node, original.Start, remainder, headLineFeeds);

		Piece inserted = AppendToAddedChunk(text);

		Piece tail = new() {
			Chunk = original.Chunk,
			Start = original.Start + remainder,
			Length = original.Length - remainder,
			LineFeeds = original.LineFeeds - headLineFeeds,
		};

		int insertedNode = InsertAfter(node, inserted);
		InsertAfter(insertedNode, tail);
	}

	private int InsertAfter(int node, Piece piece) {
		int fresh = AllocateNode(in piece);

		if (_root == Nil) {
			_root = fresh;
			_nodes[fresh].Red = false;
		}
		else if (_nodes[node].Right == Nil) {
			_nodes[node].Right = fresh;
			_nodes[fresh].Parent = node;
		}
		else {
			int successor = Leftmost(_nodes[node].Right);
			_nodes[successor].Left = fresh;
			_nodes[fresh].Parent = successor;
		}

		_length += piece.Length;
		_lineFeeds += piece.LineFeeds;
		UpdateMetadataUpward(fresh, piece.Length, piece.LineFeeds);
		InsertFixup(fresh);
		return fresh;
	}

	private int InsertBefore(int node, Piece piece) {
		int fresh = AllocateNode(in piece);

		if (_nodes[node].Left == Nil) {
			_nodes[node].Left = fresh;
			_nodes[fresh].Parent = node;
		}
		else {
			int predecessor = Rightmost(_nodes[node].Left);
			_nodes[predecessor].Right = fresh;
			_nodes[fresh].Parent = predecessor;
		}

		_length += piece.Length;
		_lineFeeds += piece.LineFeeds;
		UpdateMetadataUpward(fresh, piece.Length, piece.LineFeeds);
		InsertFixup(fresh);
		return fresh;
	}

	#endregion

	#region Delete

	public void Delete(int offset, int count) {
		if (count <= 0 || _root == Nil) return;

		if (offset < 0) {
			count += offset;
			offset = 0;
		}

		if (offset >= _length) return;

		if (offset + count > _length) count = _length - offset;

		if (count <= 0) return;

		(int startNode, int startRemainder) = FindNodeAt(offset);
		(int endNode, int endRemainder) = FindNodeAt(offset + count);

		if (startNode == endNode) {
			DeleteWithinPiece(startNode, startRemainder, endRemainder);
			return;
		}

		int collectedCount = 0;
		for (int node = Successor(startNode); node != endNode && node != Nil; node = Successor(node)) {
			collectedCount++;
		}

		int[] doomed = collectedCount > 0 ? new int[collectedCount] : [];
		int index = 0;
		for (int node = Successor(startNode); node != endNode && node != Nil; node = Successor(node)) {
			doomed[index++] = node;
		}

		Piece startPiece = _nodes[startNode].Piece;

		if (startRemainder != 0) ResizeWithinPiece(startNode, startPiece.Start, startRemainder);
		else RemoveNode(startNode);

		Piece endPiece = _nodes[endNode].Piece;

		if (endRemainder == endPiece.Length) RemoveNode(endNode);
		else if (endRemainder > 0) {
			ResizeWithinPiece(endNode, endPiece.Start + endRemainder, endPiece.Length - endRemainder);
		}

		for (int i = 0; i < doomed.Length; i++) RemoveNode(doomed[i]);

	}

	private void DeleteWithinPiece(int node, int start, int end) {
		Piece piece = _nodes[node].Piece;

		if (start == 0 && end == piece.Length) {
			RemoveNode(node);
			return;
		}

		if (start == 0) {
			ResizeWithinPiece(node, piece.Start + end, piece.Length - end);
			return;
		}

		if (end == piece.Length) {
			ResizeWithinPiece(node, piece.Start, start);
			return;
		}

		int headLineFeeds = _chunks[piece.Chunk].CountLineFeeds(piece.Start, piece.Start + start);
		int holeLineFeeds = _chunks[piece.Chunk].CountLineFeeds(piece.Start + start, piece.Start + end);

		ResizePiece(node, piece.Start, start, headLineFeeds);

		InsertAfter(node, new Piece {
			Chunk = piece.Chunk,
			Start = piece.Start + end,
			Length = piece.Length - end,
			LineFeeds = piece.LineFeeds - headLineFeeds - holeLineFeeds,
		});
	}

	private void ResizeWithinPiece(int node, int start, int length) {
		Piece piece = _nodes[node].Piece;
		TextChunk chunk = _chunks[piece.Chunk];

		int droppedBefore = chunk.CountLineFeeds(piece.Start, start);
		int droppedAfter = chunk.CountLineFeeds(start + length, piece.Start + piece.Length);

		ResizePiece(node, start, length, piece.LineFeeds - droppedBefore - droppedAfter);
	}

	private void ResizePiece(int node, int start, int length) {
		Piece piece = _nodes[node].Piece;
		ResizePiece(node, start, length, _chunks[piece.Chunk].CountLineFeeds(start, start + length));
	}

	private void ResizePiece(int node, int start, int length, int lineFeeds) {
		Piece piece = _nodes[node].Piece;

		int sizeDelta = length - piece.Length;
		int lineFeedDelta = lineFeeds - piece.LineFeeds;

		_nodes[node].Piece.Start = start;
		_nodes[node].Piece.Length = length;
		_nodes[node].Piece.LineFeeds = lineFeeds;

		_length += sizeDelta;
		_lineFeeds += lineFeedDelta;
		UpdateMetadataUpward(node, sizeDelta, lineFeedDelta);
	}

	#endregion

	#region Red-black machinery

	private void UpdateMetadataUpward(int node, int sizeDelta, int lineFeedDelta) {
		if (sizeDelta == 0 && lineFeedDelta == 0) return;

		while (node != _root && node != Nil) {
			int parent = _nodes[node].Parent;
			if (_nodes[parent].Left == node) {
				_nodes[parent].SizeLeft += sizeDelta;
				_nodes[parent].LineFeedLeft += lineFeedDelta;
			}

			node = parent;
		}
	}

	private int SubtreeSize(int node) {
		int total = 0;
		while (node != Nil) {
			total += _nodes[node].SizeLeft + _nodes[node].Piece.Length;
			node = _nodes[node].Right;
		}

		return total;
	}

	private int SubtreeLineFeeds(int node) {
		int total = 0;
		while (node != Nil) {
			total += _nodes[node].LineFeedLeft + _nodes[node].Piece.LineFeeds;
			node = _nodes[node].Right;
		}

		return total;
	}

	private void RecomputeMetadata(int node) {
		if (node == _root) return;

		while (node != _root && node == _nodes[_nodes[node].Parent].Right) node = _nodes[node].Parent;

		if (node == _root) return;

		node = _nodes[node].Parent;
		int sizeDelta = SubtreeSize(_nodes[node].Left) - _nodes[node].SizeLeft;
		int lineFeedDelta = SubtreeLineFeeds(_nodes[node].Left) - _nodes[node].LineFeedLeft;
		_nodes[node].SizeLeft += sizeDelta;
		_nodes[node].LineFeedLeft += lineFeedDelta;

		while (node != _root && (sizeDelta != 0 || lineFeedDelta != 0)) {
			int parent = _nodes[node].Parent;
			if (_nodes[parent].Left == node) {
				_nodes[parent].SizeLeft += sizeDelta;
				_nodes[parent].LineFeedLeft += lineFeedDelta;
			}

			node = parent;
		}
	}

	private void LeftRotate(int x) {
		int y = _nodes[x].Right;

		_nodes[y].SizeLeft += _nodes[x].SizeLeft + _nodes[x].Piece.Length;
		_nodes[y].LineFeedLeft += _nodes[x].LineFeedLeft + _nodes[x].Piece.LineFeeds;

		_nodes[x].Right = _nodes[y].Left;
		if (_nodes[y].Left != Nil) _nodes[_nodes[y].Left].Parent = x;

		int parent = _nodes[x].Parent;
		_nodes[y].Parent = parent;
		if (parent == Nil) _root = y;
		else if (_nodes[parent].Left == x) _nodes[parent].Left = y;
		else _nodes[parent].Right = y;

		_nodes[y].Left = x;
		_nodes[x].Parent = y;
	}

	private void RightRotate(int y) {
		int x = _nodes[y].Left;

		_nodes[y].Left = _nodes[x].Right;
		if (_nodes[x].Right != Nil) _nodes[_nodes[x].Right].Parent = y;

		_nodes[y].SizeLeft -= _nodes[x].SizeLeft + _nodes[x].Piece.Length;
		_nodes[y].LineFeedLeft -= _nodes[x].LineFeedLeft + _nodes[x].Piece.LineFeeds;

		int parent = _nodes[y].Parent;
		_nodes[x].Parent = parent;
		if (parent == Nil) _root = x;
		else if (_nodes[parent].Right == y) _nodes[parent].Right = x;
		else _nodes[parent].Left = x;

		_nodes[x].Right = y;
		_nodes[y].Parent = x;
	}

	private void InsertFixup(int z) {
		while (z != _root && _nodes[_nodes[z].Parent].Red) {
			int parent = _nodes[z].Parent;
			int grandparent = _nodes[parent].Parent;

			if (parent == _nodes[grandparent].Left) {
				int uncle = _nodes[grandparent].Right;
				if (_nodes[uncle].Red && uncle != Nil) {
					_nodes[parent].Red = false;
					_nodes[uncle].Red = false;
					_nodes[grandparent].Red = true;
					z = grandparent;
				}
				else {
					if (z == _nodes[parent].Right) {
						z = parent;
						LeftRotate(z);
						parent = _nodes[z].Parent;
						grandparent = _nodes[parent].Parent;
					}

					_nodes[parent].Red = false;
					_nodes[grandparent].Red = true;
					RightRotate(grandparent);
				}
			}
			else {
				int uncle = _nodes[grandparent].Left;
				if (_nodes[uncle].Red && uncle != Nil) {
					_nodes[parent].Red = false;
					_nodes[uncle].Red = false;
					_nodes[grandparent].Red = true;
					z = grandparent;
				}
				else {
					if (z == _nodes[parent].Left) {
						z = parent;
						RightRotate(z);
						parent = _nodes[z].Parent;
						grandparent = _nodes[parent].Parent;
					}

					_nodes[parent].Red = false;
					_nodes[grandparent].Red = true;
					LeftRotate(grandparent);
				}
			}
		}

		_nodes[_root].Red = false;
	}

	private void RemoveNode(int z) {
		_length -= _nodes[z].Piece.Length;
		_lineFeeds -= _nodes[z].Piece.LineFeeds;

		int x;
		int y;

		if (_nodes[z].Left == Nil) {
			y = z;
			x = _nodes[y].Right;
		}
		else if (_nodes[z].Right == Nil) {
			y = z;
			x = _nodes[y].Left;
		}
		else {
			y = Leftmost(_nodes[z].Right);
			x = _nodes[y].Right;
		}

		if (y == _root) {
			_root = x;
			_nodes[x].Red = false;
			_nodes[x].Parent = Nil;
			FreeNode(z);
			ResetSentinel();
			return;
		}

		bool removedRed = _nodes[y].Red;

		if (_nodes[_nodes[y].Parent].Left == y) _nodes[_nodes[y].Parent].Left = x;
		else _nodes[_nodes[y].Parent].Right = x;

		if (y == z) {
			_nodes[x].Parent = _nodes[y].Parent;
			RecomputeMetadata(x);
		}
		else {
			_nodes[x].Parent = _nodes[y].Parent == z ? y : _nodes[y].Parent;

			RecomputeMetadata(x);

			_nodes[y].Left = _nodes[z].Left;
			_nodes[y].Right = _nodes[z].Right;
			_nodes[y].Parent = _nodes[z].Parent;
			_nodes[y].Red = _nodes[z].Red;

			if (z == _root) _root = y;
			else if (_nodes[_nodes[z].Parent].Left == z) _nodes[_nodes[z].Parent].Left = y;
			else _nodes[_nodes[z].Parent].Right = y;

			if (_nodes[y].Left != Nil) _nodes[_nodes[y].Left].Parent = y;

			if (_nodes[y].Right != Nil) _nodes[_nodes[y].Right].Parent = y;

			_nodes[y].SizeLeft = _nodes[z].SizeLeft;
			_nodes[y].LineFeedLeft = _nodes[z].LineFeedLeft;
			RecomputeMetadata(y);
		}

		FreeNode(z);

		if (_nodes[x].Parent != Nil && _nodes[_nodes[x].Parent].Left == x) {
			int parent = _nodes[x].Parent;
			int newSizeLeft = SubtreeSize(x);
			int newLineFeedLeft = SubtreeLineFeeds(x);
			if (newSizeLeft != _nodes[parent].SizeLeft || newLineFeedLeft != _nodes[parent].LineFeedLeft) {
				int sizeDelta = newSizeLeft - _nodes[parent].SizeLeft;
				int lineFeedDelta = newLineFeedLeft - _nodes[parent].LineFeedLeft;
				_nodes[parent].SizeLeft = newSizeLeft;
				_nodes[parent].LineFeedLeft = newLineFeedLeft;
				UpdateMetadataUpward(parent, sizeDelta, lineFeedDelta);
			}
		}

		RecomputeMetadata(x);

		if (!removedRed) DeleteFixup(x);

		ResetSentinel();
	}

	private void DeleteFixup(int x) {
		while (x != _root && !_nodes[x].Red) {
			int parent = _nodes[x].Parent;
			if (x == _nodes[parent].Left) {
				int sibling = _nodes[parent].Right;
				if (_nodes[sibling].Red) {
					_nodes[sibling].Red = false;
					_nodes[parent].Red = true;
					LeftRotate(parent);
					parent = _nodes[x].Parent;
					sibling = _nodes[parent].Right;
				}

				if (!_nodes[_nodes[sibling].Left].Red && !_nodes[_nodes[sibling].Right].Red) {
					_nodes[sibling].Red = true;
					x = parent;
				}
				else {
					if (!_nodes[_nodes[sibling].Right].Red) {
						_nodes[_nodes[sibling].Left].Red = false;
						_nodes[sibling].Red = true;
						RightRotate(sibling);
						parent = _nodes[x].Parent;
						sibling = _nodes[parent].Right;
					}

					_nodes[sibling].Red = _nodes[parent].Red;
					_nodes[parent].Red = false;
					_nodes[_nodes[sibling].Right].Red = false;
					LeftRotate(parent);
					x = _root;
				}
			}
			else {
				int sibling = _nodes[parent].Left;
				if (_nodes[sibling].Red) {
					_nodes[sibling].Red = false;
					_nodes[parent].Red = true;
					RightRotate(parent);
					parent = _nodes[x].Parent;
					sibling = _nodes[parent].Left;
				}

				if (!_nodes[_nodes[sibling].Left].Red && !_nodes[_nodes[sibling].Right].Red) {
					_nodes[sibling].Red = true;
					x = parent;
				}
				else {
					if (!_nodes[_nodes[sibling].Left].Red) {
						_nodes[_nodes[sibling].Right].Red = false;
						_nodes[sibling].Red = true;
						LeftRotate(sibling);
						parent = _nodes[x].Parent;
						sibling = _nodes[parent].Left;
					}

					_nodes[sibling].Red = _nodes[parent].Red;
					_nodes[parent].Red = false;
					_nodes[_nodes[sibling].Left].Red = false;
					RightRotate(parent);
					x = _root;
				}
			}
		}

		_nodes[x].Red = false;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ResetSentinel() {
		_nodes[Nil].Parent = Nil;
		_nodes[Nil].Left = Nil;
		_nodes[Nil].Right = Nil;
		_nodes[Nil].Red = false;
	}

	#endregion

	#region Diagnostics

	internal void Validate() {
		if (_nodes[Nil].Red) throw new InvalidOperationException("Sentinel must be black.");

		if (_root != Nil && _nodes[_root].Red) throw new InvalidOperationException("Root must be black.");

		if (_root != Nil && _nodes[_root].Parent != Nil) {
			throw new InvalidOperationException("Root must have no parent.");
		}

		int totalLength = 0;
		int totalLineFeeds = 0;
		for (int node = Leftmost(_root); node != Nil; node = Successor(node)) {
			Piece piece = _nodes[node].Piece;
			if (piece.Length <= 0) throw new InvalidOperationException($"Node {node} holds an empty piece.");

			int expected = _chunks[piece.Chunk].CountLineFeeds(piece.Start, piece.Start + piece.Length);
			if (piece.LineFeeds != expected) {
				throw new InvalidOperationException($"Node {node} line-feed count is {piece.LineFeeds}, expected {expected}.");
			}

			if (_nodes[node].SizeLeft != SubtreeSize(_nodes[node].Left)) {
				throw new InvalidOperationException($"Node {node} has stale SizeLeft.");
			}

			if (_nodes[node].LineFeedLeft != SubtreeLineFeeds(_nodes[node].Left)) {
				throw new InvalidOperationException($"Node {node} has stale LineFeedLeft.");
			}

			totalLength += piece.Length;
			totalLineFeeds += piece.LineFeeds;
		}

		if (totalLength != _length) {
			throw new InvalidOperationException($"Length is {_length}, pieces sum to {totalLength}.");
		}

		if (totalLineFeeds != _lineFeeds) {
			throw new InvalidOperationException($"Line-feed count is {_lineFeeds}, pieces sum to {totalLineFeeds}.");
		}

		ValidateBlackHeight(_root);
	}

	private int ValidateBlackHeight(int node) {
		if (node == Nil) return 1;

		if (_nodes[node].Red) {
			if (_nodes[_nodes[node].Left].Red || _nodes[_nodes[node].Right].Red) {
				throw new InvalidOperationException($"Red node {node} has a red child.");
			}
		}

		int left = ValidateBlackHeight(_nodes[node].Left);
		int right = ValidateBlackHeight(_nodes[node].Right);
		if (left != right) {
			throw new InvalidOperationException($"Black height differs under node {node}: {left} vs {right}.");
		}

		return left + (_nodes[node].Red ? 0 : 1);
	}

	#endregion
}
