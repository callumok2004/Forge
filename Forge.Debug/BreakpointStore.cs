using System.Text.Json;
using System.Text.Json.Nodes;

using Forge.Common.Debug;

namespace Forge.Debug;

public sealed class BreakpointStore
{
	private readonly List<SourceBreakpoint> _source = [];
	private readonly List<FunctionBreakpoint> _functions = [];
	private readonly List<ExceptionBreakpoint> _exceptions = [];
	private readonly Lock _sync = new();

	private string? _path;

	public bool Activated { get; private set; } = true;

	public event Action? Changed;

	public IReadOnlyList<SourceBreakpoint> Source {
		get { lock (_sync) return [.. _source]; }
	}

	public IReadOnlyList<FunctionBreakpoint> Functions {
		get { lock (_sync) return [.. _functions]; }
	}

	public IReadOnlyList<ExceptionBreakpoint> Exceptions {
		get { lock (_sync) return [.. _exceptions]; }
	}

	public IReadOnlyList<SourceBreakpoint> For(string path) {
		lock (_sync) {
			List<SourceBreakpoint> matches = [];

			foreach (SourceBreakpoint breakpoint in _source) {
				if (Same(breakpoint.Path, path)) matches.Add(breakpoint);
			}

			matches.Sort(static (left, right) => left.Line.CompareTo(right.Line));
			return matches;
		}
	}

	public IReadOnlyList<string> Paths {
		get {
			lock (_sync) {
				HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

				foreach (SourceBreakpoint breakpoint in _source) paths.Add(breakpoint.Path);

				return [.. paths];
			}
		}
	}

	public bool Toggle(string path, int line) {
		bool added;

		lock (_sync) {
			int index = IndexOf(path, line);

			if (index >= 0) {
				_source.RemoveAt(index);
				added = false;
			}
			else {
				_source.Add(new SourceBreakpoint(path, line));
				added = true;
			}
		}

		Notify();
		return added;
	}

	public void Add(SourceBreakpoint breakpoint) {
		lock (_sync) {
			int index = IndexOf(breakpoint.Path, breakpoint.Line);

			if (index >= 0) _source[index] = breakpoint;
			else _source.Add(breakpoint);
		}

		Notify();
	}

	public void Update(string path, int line, string? condition, string? hitCondition, string? logMessage) {
		lock (_sync) {
			int index = IndexOf(path, line);

			if (index < 0) {
				_source.Add(new SourceBreakpoint(path, line, condition, hitCondition, logMessage));
			}
			else {
				_source[index] = _source[index] with {
					Condition = condition,
					HitCondition = hitCondition,
					LogMessage = logMessage,
				};
			}
		}

		Notify();
	}

	public void Remove(string path, int line) {
		lock (_sync) {
			int index = IndexOf(path, line);

			if (index < 0) return;

			_source.RemoveAt(index);
		}

		Notify();
	}

	public void RemoveAll() {
		lock (_sync) {
			_source.Clear();
			_functions.Clear();
		}

		Notify();
	}

	public void SetEnabled(bool enabled, string? path, int line) {
		lock (_sync) {
			if (path is null) {
				for (int i = 0; i < _source.Count; i++) _source[i] = _source[i] with { Enabled = enabled };
			}
			else {
				int index = IndexOf(path, line);

				if (index < 0) return;

				_source[index] = _source[index] with { Enabled = enabled };
			}
		}

		Notify();
	}

	public void SetActivated(bool activated) {
		if (Activated == activated) return;

		Activated = activated;
		Notify();
	}

	public void Verify(string path, IReadOnlyList<(int Line, bool Verified, int? ActualLine, string? Message)> results) {
		lock (_sync) {
			foreach ((int line, bool verified, int? actual, string? message) in results) {
				int index = IndexOf(path, line);

				if (index < 0) continue;

				_source[index] = _source[index] with {
					Verified = verified,
					VerifiedLine = actual,
					Message = message,
				};
			}
		}

		Notify();
	}

	public void AddFunction(string name) {
		lock (_sync) {
			foreach (FunctionBreakpoint existing in _functions) {
				if (string.Equals(existing.Name, name, StringComparison.Ordinal)) return;
			}

			_functions.Add(new FunctionBreakpoint(name));
		}

		Notify();
	}

	public void RemoveFunction(string name) {
		lock (_sync) {
			_functions.RemoveAll(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
		}

		Notify();
	}

	public void SetExceptionFilters(IReadOnlyList<ExceptionBreakpoint> filters) {
		lock (_sync) {
			List<ExceptionBreakpoint> merged = [];

			foreach (ExceptionBreakpoint filter in filters) {
				ExceptionBreakpoint? previous = null;

				foreach (ExceptionBreakpoint existing in _exceptions) {
					if (string.Equals(existing.Filter, filter.Filter, StringComparison.Ordinal)) {
						previous = existing;
						break;
					}
				}

				merged.Add(previous is null ? filter : filter with {
					Enabled = previous.Enabled,
					Condition = previous.Condition,
				});
			}

			_exceptions.Clear();
			_exceptions.AddRange(merged);
		}

		Notify();
	}

	public void SetException(string filter, bool enabled, string? condition) {
		lock (_sync) {
			for (int i = 0; i < _exceptions.Count; i++) {
				if (!string.Equals(_exceptions[i].Filter, filter, StringComparison.Ordinal)) continue;

				_exceptions[i] = _exceptions[i] with { Enabled = enabled, Condition = condition };
				break;
			}
		}

		Notify();
	}

	public void Load(string path) {
		_path = path;

		if (!File.Exists(path)) return;

		try {
			if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return;

			lock (_sync) {
				_source.Clear();
				_functions.Clear();

				Activated = root["activated"]?.GetValue<bool>() ?? true;

				if (root["source"] is JsonArray source) {
					foreach (JsonNode? entry in source) {
						if (entry?["path"]?.GetValue<string>() is not { } file) continue;

						_source.Add(new SourceBreakpoint(file,
							entry["line"]?.GetValue<int>() ?? 1,
							entry["condition"]?.GetValue<string>(),
							entry["hitCondition"]?.GetValue<string>(),
							entry["logMessage"]?.GetValue<string>(),
							entry["enabled"]?.GetValue<bool>() ?? true));
					}
				}

				if (root["functions"] is JsonArray functions) {
					foreach (JsonNode? entry in functions) {
						if (entry?["name"]?.GetValue<string>() is not { } name) continue;

						_functions.Add(new FunctionBreakpoint(name,
							entry["condition"]?.GetValue<string>(),
							entry["hitCondition"]?.GetValue<string>(),
							entry["enabled"]?.GetValue<bool>() ?? true));
					}
				}
			}
		}
		catch (Exception exception) when (exception is JsonException or IOException
			or UnauthorizedAccessException) {
		}
	}

	public void Save() {
		if (_path is not { Length: > 0 } path) return;

		JsonArray source = [];
		JsonArray functions = [];

		lock (_sync) {
			foreach (SourceBreakpoint breakpoint in _source) {
				source.Add(new JsonObject {
					["path"] = breakpoint.Path,
					["line"] = breakpoint.Line,
					["condition"] = breakpoint.Condition,
					["hitCondition"] = breakpoint.HitCondition,
					["logMessage"] = breakpoint.LogMessage,
					["enabled"] = breakpoint.Enabled,
				});
			}

			foreach (FunctionBreakpoint breakpoint in _functions) {
				functions.Add(new JsonObject {
					["name"] = breakpoint.Name,
					["condition"] = breakpoint.Condition,
					["hitCondition"] = breakpoint.HitCondition,
					["enabled"] = breakpoint.Enabled,
				});
			}
		}

		JsonObject root = new() {
			["activated"] = Activated,
			["source"] = source,
			["functions"] = functions,
		};

		try {
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
		}
	}

	private int IndexOf(string path, int line) {
		for (int i = 0; i < _source.Count; i++) {
			if (!Same(_source[i].Path, path)) continue;

			if (_source[i].Line == line || _source[i].EffectiveLine == line) return i;
		}

		return -1;
	}

	private static bool Same(string left, string right) =>
		string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

	private void Notify() {
		Save();
		Changed?.Invoke();
	}
}
