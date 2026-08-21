using Forge.Common.Commands;

namespace Forge.Commands;

public sealed class OutputChannel(string name) : IOutputChannel
{
	private const int MaxLines = 5000;

	private readonly Queue<string> _lines = new();
	private readonly Lock _sync = new();

	private Action<IOutputChannel>? _appended;

	public string GetName() => name;

	public int GetCount() {
		lock (_sync) return _lines.Count;
	}

	public void OnAppended(Action<IOutputChannel> handler) {
		_appended -= handler;
		_appended += handler;
	}

	public void Append(string line) {
		lock (_sync) {
			foreach (string part in line.ReplaceLineEndings("\n").Split('\n')) {
				_lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {part}");

				while (_lines.Count > MaxLines) _lines.Dequeue();
			}
		}

		_appended?.Invoke(this);
	}

	public void Clear() {
		lock (_sync) _lines.Clear();

		_appended?.Invoke(this);
	}

	public int CopyTo(string[] destination, int skip) {
		lock (_sync) {
			int written = 0;
			int index = 0;

			foreach (string line in _lines) {
				if (index++ < skip) continue;

				if (written == destination.Length) break;

				destination[written++] = line;
			}

			return written;
		}
	}

	public string ReadAll() {
		lock (_sync) return string.Join(Environment.NewLine, _lines);
	}

	public string ReadRange(int first, int last) {
		lock (_sync) {
			List<string> taken = [];
			int index = 0;

			foreach (string line in _lines) {
				if (index >= first && index <= last) taken.Add(line);

				index++;
			}

			return string.Join(Environment.NewLine, taken);
		}
	}
}

public sealed class OutputChannels : IOutputChannels
{
	private readonly Lock _sync = new();

	private OutputChannel[] _channels = [];
	private Action? _added;

	public IReadOnlyList<IOutputChannel> All() => _channels;

	public void OnAdded(Action handler) => _added += handler;

	public IOutputChannel Get(string name) {
		foreach (OutputChannel existing in _channels) {
			if (existing.GetName() == name) return existing;
		}

		OutputChannel created;

		lock (_sync) {
			foreach (OutputChannel existing in _channels) {
				if (existing.GetName() == name) return existing;
			}

			created = new OutputChannel(name);
			_channels = [.. _channels, created];
		}

		_added?.Invoke();
		return created;
	}
}
