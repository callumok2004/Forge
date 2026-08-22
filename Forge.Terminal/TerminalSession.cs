namespace Forge.Terminal;

public sealed class TerminalSession : IDisposable
{
	private const char Escape = '\u001B';

	private const char Newline = (char)10;
	private const char Return = (char)13;
	private const char Backspace = (char)8;
	private const char Bell = (char)7;

	private readonly ConPtyHost _host = new();
	private readonly System.Text.StringBuilder _line = new();
	private readonly System.Collections.Concurrent.ConcurrentQueue<string> _pending = new();
	private readonly System.Text.StringBuilder _observed = new();

	public TerminalHistory? History { get; set; }

	public string? Suggestion { get; private set; }

	public ReadOnlySpan<char> SuggestionRemainder =>
		Suggestion is { } suggestion && suggestion.Length > _line.Length
			? suggestion.AsSpan(_line.Length)
			: default;

	public event Action? Changed;

	public Action<string>? LineObserver { get; set; }

	public Action<int>? ExitObserver { get; set; }

	public TerminalScreen Screen { get; } = new();

	public bool IsRunning => _host.IsRunning;

	public bool HasExited { get; private set; }

	public string Title { get; private set; } = "Terminal";

	public bool Start(TerminalProfile profile, string workingDirectory, int columns, int rows) {
		Screen.Resize(columns, rows);
		Title = profile.Name;

		_host.Output += text => {
			_pending.Enqueue(text);
			Observe(text);
			Changed?.Invoke();
		};

		_host.Exited += () => {
			HasExited = true;
			Flush();
			_pending.Enqueue("\r\n[process exited]\r\n");
			ExitObserver?.Invoke(_host.ExitCode);
			Changed?.Invoke();
		};

		return _host.Start(profile.CommandLine, workingDirectory, columns, rows);
	}

	private void Observe(ReadOnlySpan<char> text) {
		if (LineObserver is null) return;

		for (int i = 0; i < text.Length; i++) {
			char character = text[i];

			if (character == Escape) {
				while (i < text.Length && !char.IsLetter(text[i])) i++;
				continue;
			}

			if (character == Newline) {
				Flush();
				continue;
			}

			if (character is Return or Backspace or Bell) continue;

			_observed.Append(character);
		}
	}

	private void Flush() {
		if (_observed.Length == 0) return;

		string line = _observed.ToString();
		_observed.Clear();
		LineObserver?.Invoke(line);
	}

	public void Drain() {
		while (_pending.TryDequeue(out string? text)) Screen.Write(text);
	}

	public void Resize(int columns, int rows) {
		if (columns == Screen.Columns && rows == Screen.Rows) return;

		Screen.Resize(columns, rows);
		_host.Resize(columns, rows);
	}

	public void Write(ReadOnlySpan<char> text) {
		Screen.ScrollToBottom();
		Record(text);
		_host.Write(text);
	}

	public bool AcceptSuggestion() {
		if (SuggestionRemainder.IsEmpty) return false;

		Write(SuggestionRemainder.ToString());
		return true;
	}

	private void Record(ReadOnlySpan<char> text) {
		foreach (char character in text) {
			switch (character) {
				case '\r':
				case '\n':
					if (_line.Length > 0) {
						History?.Add(_line.ToString());
						_line.Clear();
					}

					break;

				case '\b':
				case '\u007F':
					if (_line.Length > 0) _line.Length--;

					break;

				case Escape:
					_line.Clear();
					break;

				default:
					if (character >= ' ') _line.Append(character);
					else _line.Clear();

					break;
			}
		}

		Suggestion = History?.Suggest(_line.ToString());
	}

	public bool SendKey(TerminalKey key, bool control) {
		ReadOnlySpan<char> sequence = key switch {
			TerminalKey.Enter => "\r",
			TerminalKey.Backspace => "\u007F",
			TerminalKey.Tab => "\t",
			TerminalKey.Escape => "\u001B",

			TerminalKey.Up => control ? $"{Escape}[1;5A" : $"{Escape}[A",
			TerminalKey.Down => control ? $"{Escape}[1;5B" : $"{Escape}[B",
			TerminalKey.Right => control ? $"{Escape}[1;5C" : $"{Escape}[C",
			TerminalKey.Left => control ? $"{Escape}[1;5D" : $"{Escape}[D",
			TerminalKey.Home => $"{Escape}[H",
			TerminalKey.End => $"{Escape}[F",
			TerminalKey.Delete => $"{Escape}[3~",
			TerminalKey.PageUp => $"{Escape}[5~",
			TerminalKey.PageDown => $"{Escape}[6~",

			_ => default,
		};

		if (sequence.IsEmpty) return false;

		Write(sequence);
		return true;
	}

	public void SendControl(char letter) {
		char upper = char.ToUpperInvariant(letter);

		if (upper is >= 'A' and <= 'Z') Write([(char)(upper - 'A' + 1)]);
	}

	public void Dispose() => _host.Dispose();
}

public enum TerminalKey
{
	None,
	Enter,
	Backspace,
	Tab,
	Escape,
	Up,
	Down,
	Left,
	Right,
	Home,
	End,
	Delete,
	PageUp,
	PageDown,
}
