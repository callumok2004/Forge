namespace Forge.Terminal;

public struct TerminalCell
{
	public char Character;

	public int Foreground;

	public int Background;

	public bool Bold;
}

public sealed class TerminalScreen
{
	private const int ScrollbackLimit = 5000;

	private const int MaxParameters = 16;

	private const char EscapeCharacter = '\u001B';

	private readonly List<TerminalCell[]> _lines = [];
	private readonly int[] _parameters = new int[MaxParameters];

	private State _state;
	private int _parameterCount;
	private bool _private;

	private int _cursorColumn;
	private int _cursorRow;
	private int _savedColumn;
	private int _savedRow;

	private int _foreground;
	private int _background;
	private bool _bold;
	private bool _inverse;

	public object Sync { get; } = new();

	public int Columns { get; private set; } = 80;

	public int Rows { get; private set; } = 24;

	public bool CursorVisible { get; private set; } = true;

	public int ScrollOffset { get; private set; }

	public int ScrollbackCount => Math.Max(0, _lines.Count - Rows);

	public int CursorColumn => _cursorColumn;

	public int CursorRow => _cursorRow;

	public TerminalScreen() => Resize(80, 24);

	public ReadOnlySpan<TerminalCell> Row(int row) {
		int index = _lines.Count - Rows - ScrollOffset + row;
		return index >= 0 && index < _lines.Count ? _lines[index] : default;
	}

	public void ScrollBy(int lines) {
		ScrollOffset = Math.Clamp(ScrollOffset + lines, 0, ScrollbackCount);
	}

	public void ScrollToBottom() => ScrollOffset = 0;

	public void Resize(int columns, int rows) {
		columns = Math.Max(1, columns);
		rows = Math.Max(1, rows);

		if (columns == Columns && rows == Rows && _lines.Count > 0) return;

		Columns = columns;
		Rows = rows;

		for (int i = 0; i < _lines.Count; i++) {
			if (_lines[i].Length != columns) {
				TerminalCell[] line = NewLine();
				Array.Copy(_lines[i], line, Math.Min(_lines[i].Length, columns));
				_lines[i] = line;
			}
		}

		while (_lines.Count < rows) _lines.Add(NewLine());

		_cursorRow = Math.Clamp(_cursorRow, 0, rows - 1);
		_cursorColumn = Math.Clamp(_cursorColumn, 0, columns - 1);
	}

	private TerminalCell[] NewLine() {
		TerminalCell[] line = new TerminalCell[Columns];

		for (int i = 0; i < line.Length; i++) line[i].Character = ' ';

		return line;
	}

	public void Write(ReadOnlySpan<char> text) {
		lock (Sync) {
			ScrollOffset = 0;

			foreach (char character in text) Consume(character);
		}
	}

	private enum State
	{
		Ground,
		Escape,

		Parameters,

		String,

		StringEscape,
	}

	private void Consume(char character) {
		switch (_state) {
			case State.Ground:
				Ground(character);
				return;

			case State.Escape:
				Escape(character);
				return;

			case State.Parameters:
				Parameters(character);
				return;

			case State.String:
				if (character == '\a') _state = State.Ground;
				else if (character == EscapeCharacter) _state = State.StringEscape;

				return;

			case State.StringEscape:
				_state = character == '\\' ? State.Ground : State.String;
				return;
		}
	}

	private void Ground(char character) {
		switch (character) {
			case EscapeCharacter:
				_state = State.Escape;
				return;

			case '\r':
				_cursorColumn = 0;
				return;

			case '\n':
				NextLine();
				return;

			case '\b':
				_cursorColumn = Math.Max(0, _cursorColumn - 1);
				return;

			case '\t':
				_cursorColumn = Math.Min(Columns - 1, (_cursorColumn + 8) & ~7);
				return;

			case '\a':
				return;
		}

		if (character < ' ') return;

		Put(character);
	}

	private void Escape(char character) {
		switch (character) {
			case '[':
				_parameterCount = 0;
				_parameters[0] = 0;
				_private = false;
				_state = State.Parameters;
				return;

			case ']':
				_state = State.String;
				return;

			case 'M':
				if (_cursorRow == 0) {
					int top = _lines.Count - Rows;
					_lines.Insert(Math.Max(0, top), NewLine());
					Trim();
				}
				else _cursorRow--;

				_state = State.Ground;
				return;

			case '7':
				Save();
				_state = State.Ground;
				return;

			case '8':
				Restore();
				_state = State.Ground;
				return;

			default:
				_state = State.Ground;
				return;
		}
	}

	private void Parameters(char character) {
		if (character == '?' || character == '<' || character == '>' || character == '!') {
			_private = true;
			return;
		}

		if (character is >= '0' and <= '9') {
			if (_parameterCount < MaxParameters) {
				_parameters[_parameterCount] = (_parameters[_parameterCount] * 10) + (character - '0');
			}

			return;
		}

		if (character == ';') {
			if (_parameterCount < MaxParameters - 1) _parameters[++_parameterCount] = 0;

			return;
		}

		if (character is >= ' ' and <= '/') return;

		_parameterCount++;
		Dispatch(character);
		_state = State.Ground;
	}

	private int Parameter(int index, int fallback) =>
		index < _parameterCount && _parameters[index] > 0 ? _parameters[index] : fallback;

	private void Dispatch(char final) {
		switch (final) {
			case 'A':
				_cursorRow = Math.Max(0, _cursorRow - Parameter(0, 1));
				return;

			case 'B':
				_cursorRow = Math.Min(Rows - 1, _cursorRow + Parameter(0, 1));
				return;

			case 'C':
				_cursorColumn = Math.Min(Columns - 1, _cursorColumn + Parameter(0, 1));
				return;

			case 'D':
				_cursorColumn = Math.Max(0, _cursorColumn - Parameter(0, 1));
				return;

			case 'E':
				_cursorRow = Math.Min(Rows - 1, _cursorRow + Parameter(0, 1));
				_cursorColumn = 0;
				return;

			case 'F':
				_cursorRow = Math.Max(0, _cursorRow - Parameter(0, 1));
				_cursorColumn = 0;
				return;

			case 'G':
				_cursorColumn = Math.Clamp(Parameter(0, 1) - 1, 0, Columns - 1);
				return;

			case 'd':
				_cursorRow = Math.Clamp(Parameter(0, 1) - 1, 0, Rows - 1);
				return;

			case 'H':
			case 'f':
				_cursorRow = Math.Clamp(Parameter(0, 1) - 1, 0, Rows - 1);
				_cursorColumn = Math.Clamp(Parameter(1, 1) - 1, 0, Columns - 1);
				return;

			case 'J':
				EraseDisplay(Parameter(0, 0) == 0 && _parameterCount > 0 ? _parameters[0] : Parameter(0, 0));
				return;

			case 'K':
				EraseLine(_parameterCount > 0 ? _parameters[0] : 0);
				return;

			case 'L':
				InsertLines(Parameter(0, 1));
				return;

			case 'M':
				DeleteLines(Parameter(0, 1));
				return;

			case '@':
				InsertCharacters(Parameter(0, 1));
				return;

			case 'P':
				DeleteCharacters(Parameter(0, 1));
				return;

			case 'X':
				EraseCharacters(Parameter(0, 1));
				return;

			case 'm':
				SelectGraphics();
				return;

			case 'h':
			case 'l':
				if (_private && _parameterCount > 0 && _parameters[0] == 25) CursorVisible = final == 'h';

				return;

			case 's':
				Save();
				return;

			case 'u':
				Restore();
				return;
		}
	}

	private void Save() {
		_savedColumn = _cursorColumn;
		_savedRow = _cursorRow;
	}

	private void Restore() {
		_cursorColumn = _savedColumn;
		_cursorRow = _savedRow;
	}

	#region Writing cells

	private void Put(char character) {
		if (_cursorColumn >= Columns) {
			_cursorColumn = 0;
			NextLine();
		}

		TerminalCell[] line = VisibleLine(_cursorRow);

		line[_cursorColumn].Character = character;
		line[_cursorColumn].Foreground = _inverse ? _background : _foreground;
		line[_cursorColumn].Background = _inverse ? _foreground : _background;
		line[_cursorColumn].Bold = _bold;

		_cursorColumn++;
	}

	private TerminalCell[] VisibleLine(int row) {
		int index = _lines.Count - Rows + row;
		return _lines[Math.Clamp(index, 0, _lines.Count - 1)];
	}

	private void NextLine() {
		if (_cursorRow < Rows - 1) {
			_cursorRow++;
			return;
		}

		_lines.Add(NewLine());
		Trim();
	}

	private void Trim() {
		int excess = _lines.Count - Rows - ScrollbackLimit;

		if (excess > 0) _lines.RemoveRange(0, excess);
	}

	private void EraseDisplay(int mode) {
		switch (mode) {
			case 0:
				EraseLine(0);

				for (int row = _cursorRow + 1; row < Rows; row++) Clear(VisibleLine(row), 0, Columns);

				return;

			case 1:
				EraseLine(1);

				for (int row = 0; row < _cursorRow; row++) Clear(VisibleLine(row), 0, Columns);

				return;

			default:
				for (int row = 0; row < Rows; row++) Clear(VisibleLine(row), 0, Columns);

				if (mode == 3 && _lines.Count > Rows) _lines.RemoveRange(0, _lines.Count - Rows);

				return;
		}
	}

	private void EraseLine(int mode) {
		TerminalCell[] line = VisibleLine(_cursorRow);

		switch (mode) {
			case 0:
				Clear(line, _cursorColumn, Columns - _cursorColumn);
				return;

			case 1:
				Clear(line, 0, Math.Min(Columns, _cursorColumn + 1));
				return;

			default:
				Clear(line, 0, Columns);
				return;
		}
	}

	private void EraseCharacters(int count) =>
		Clear(VisibleLine(_cursorRow), _cursorColumn, Math.Min(count, Columns - _cursorColumn));

	private void InsertCharacters(int count) {
		TerminalCell[] line = VisibleLine(_cursorRow);
		count = Math.Min(count, Columns - _cursorColumn);

		Array.Copy(line, _cursorColumn, line, _cursorColumn + count,
			Columns - _cursorColumn - count);

		Clear(line, _cursorColumn, count);
	}

	private void DeleteCharacters(int count) {
		TerminalCell[] line = VisibleLine(_cursorRow);
		count = Math.Min(count, Columns - _cursorColumn);

		Array.Copy(line, _cursorColumn + count, line, _cursorColumn,
			Columns - _cursorColumn - count);

		Clear(line, Columns - count, count);
	}

	private void InsertLines(int count) {
		int top = _lines.Count - Rows;

		for (int i = 0; i < count && _cursorRow + i < Rows; i++) {
			_lines.Insert(top + _cursorRow, NewLine());
			_lines.RemoveAt(_lines.Count - 1);
		}
	}

	private void DeleteLines(int count) {
		int top = _lines.Count - Rows;

		for (int i = 0; i < count && _cursorRow + i < Rows; i++) {
			_lines.RemoveAt(top + _cursorRow);
			_lines.Add(NewLine());
		}
	}

	private void Clear(TerminalCell[] line, int start, int count) {
		for (int i = start; i < start + count && i < line.Length; i++) {
			line[i].Character = ' ';
			line[i].Foreground = 0;

			line[i].Background = _inverse ? _foreground : _background;
			line[i].Bold = false;
		}
	}

	#endregion

	#region Colour

	private void SelectGraphics() {
		if (_parameterCount == 0) {
			ResetGraphics();
			return;
		}

		for (int i = 0; i < _parameterCount; i++) {
			int code = _parameters[i];

			switch (code) {
				case 0:
					ResetGraphics();
					break;

				case 1:
					_bold = true;
					break;

				case 22:
					_bold = false;
					break;

				case 7:
					_inverse = true;
					break;

				case 27:
					_inverse = false;
					break;

				case 39:
					_foreground = 0;
					break;

				case 49:
					_background = 0;
					break;

				case >= 30 and <= 37:
					_foreground = TerminalPalette.Ansi(code - 30);
					break;

				case >= 90 and <= 97:
					_foreground = TerminalPalette.Ansi(code - 90 + 8);
					break;

				case >= 40 and <= 47:
					_background = TerminalPalette.Ansi(code - 40);
					break;

				case >= 100 and <= 107:
					_background = TerminalPalette.Ansi(code - 100 + 8);
					break;

				case 38:
				case 48:
					int colour = ReadExtendedColour(ref i);

					if (code == 38) _foreground = colour;
					else _background = colour;

					break;
			}
		}
	}

	private int ReadExtendedColour(ref int index) {
		if (index + 1 >= _parameterCount) return 0;

		int kind = _parameters[index + 1];

		if (kind == 5 && index + 2 < _parameterCount) {
			index += 2;
			return TerminalPalette.Indexed(_parameters[index]);
		}

		if (kind == 2 && index + 4 < _parameterCount) {
			int red = _parameters[index + 2];
			int green = _parameters[index + 3];
			int blue = _parameters[index + 4];
			index += 4;

			return unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue;
		}

		index = _parameterCount;
		return 0;
	}

	private void ResetGraphics() {
		_foreground = 0;
		_background = 0;
		_bold = false;
		_inverse = false;
	}

	#endregion
}
