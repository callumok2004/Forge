using System.Text.RegularExpressions;

using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Parsing;

namespace Forge.Extensions.GLua.Index;

public sealed partial class GLuaIndex : IDisposable
{
	private const int ChangeDelayMilliseconds = 1000;
	private const int WatchDelayMilliseconds = 150;

	private readonly GLuaCatalog _catalog;
	private readonly Func<string?> _activePath;
	private readonly string? _excludedRoot;
	private readonly Lock _sync = new();
	private readonly Dictionary<string, ParsedFile> _files = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, string> _open = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Dictionary<string, Timer> _pending = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly HashSet<string> _addons = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly HashSet<string> _counted = [with(StringComparer.OrdinalIgnoreCase)];
	private FileSystemWatcher? _watcher;
	private int _version;
	private (int Version, string? Active, FileTokenData Data)? _compiled;
	private bool _disposed;

	public GLuaIndex(GLuaCatalog catalog, Func<string?> activePath, string? excludedRoot) {
		_catalog = catalog;
		_activePath = activePath;
		_excludedRoot = excludedRoot;
	}

	public bool BrowsingServer { get; private set; }

	public int ParsedCount { get { lock (_sync) return _counted.Count; } }

	public event Action<string>? FileParsed;

	public event Action? CountChanged;

	[GeneratedRegex(@"garrysmod(?:(?:\\|\/)addons)?(?:\\|\/)?$", RegexOptions.IgnoreCase)]
	private static partial Regex ServerFolder();

	[GeneratedRegex(@"(^.*?garrysmod(?:\\|\/)(?:addons|gamemodes)(?:\\|\/)[^\\\/]+).*$", RegexOptions.IgnoreCase)]
	private static partial Regex Addon();

	public static string? AddonFor(string path) => Addon().Match(path) is { Success: true } match ? match.Groups[1].Value : null;

	public void Start(string? workspace, IEnumerable<string> openPaths) {
		List<string> folders = [];

		if (workspace is not null && Directory.Exists(workspace)) {
			if (ServerFolder().IsMatch(workspace) || Directory.Exists(Path.Combine(workspace, "garrysmod", "addons")) || Directory.Exists(Path.Combine(workspace, "addons"))) BrowsingServer = true;
			else folders.Add(workspace);
		}

		foreach (string path in openPaths) {
			if (BrowsingServer) TryParseAddon(path);
		}

		foreach (string folder in folders) {
			foreach (string file in EnumerateLua(folder)) {
				if (_disposed) return;

				lock (_sync) {
					if (_open.ContainsKey(file)) continue;
				}

				ParseDisk(file);
			}
		}

		if (workspace is not null && Directory.Exists(workspace)) Watch(workspace);
	}

	private static IEnumerable<string> EnumerateLua(string folder) {
		try {
			return Directory.EnumerateFiles(folder, "*.lua", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive });
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	private void TryParseAddon(string path) {
		if (AddonFor(path) is not { } addon) return;

		lock (_sync) {
			if (!_addons.Add(addon)) return;
		}

		foreach (string file in EnumerateLua(addon)) {
			bool skip;

			lock (_sync) skip = _files.ContainsKey(file) || _open.ContainsKey(file);

			if (!skip) ParseDisk(file);
		}
	}

	private bool InScope(string path) {
		if (!BrowsingServer) return true;

		lock (_sync) return AddonFor(path) is { } addon && _addons.Contains(addon);
	}

	private void Watch(string root) {
		_watcher = new FileSystemWatcher(root, "*.lua") {
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
			InternalBufferSize = 64 * 1024,
		};
		_watcher.Created += (_, change) => Schedule(change.FullPath, WatchDelayMilliseconds, fromDisk: true);
		_watcher.Changed += (_, change) => Schedule(change.FullPath, WatchDelayMilliseconds, fromDisk: true);
		_watcher.Deleted += (_, change) => Schedule(change.FullPath, WatchDelayMilliseconds, fromDisk: true);
		_watcher.Renamed += (_, change) => {
			Schedule(change.OldFullPath, WatchDelayMilliseconds, fromDisk: true);
			Schedule(change.FullPath, WatchDelayMilliseconds, fromDisk: true);
		};
		_watcher.Error += (_, _) => Rescan(root);
		_watcher.EnableRaisingEvents = true;
	}

	private void Rescan(string root) {
		ThreadPool.QueueUserWorkItem(_ => {
			HashSet<string> present = [with(StringComparer.OrdinalIgnoreCase), .. EnumerateLua(root)];
			List<string> known;

			lock (_sync) known = [.. _files.Keys];

			foreach (string file in known) {
				if (!present.Contains(file)) Schedule(file, 0, fromDisk: true);
			}

			foreach (string file in present) Schedule(file, 0, fromDisk: true);
		});
	}

	private void Schedule(string path, int delay, bool fromDisk) {
		if (_disposed) return;

		lock (_sync) {
			if (fromDisk && _open.ContainsKey(path)) return;

			if (_pending.Remove(path, out Timer? existing)) existing.Dispose();

			Timer timer = new(_ => Run(path, fromDisk));
			_pending[path] = timer;
			timer.Change(delay, Timeout.Infinite);
		}
	}

	private void Run(string path, bool fromDisk) {
		string? text;

		lock (_sync) {
			if (_pending.Remove(path, out Timer? timer)) timer.Dispose();

			if (_disposed) return;

			_open.TryGetValue(path, out text);
		}

		if (text is not null) {
			Parse(path, text, keepOnError: true);
			return;
		}

		if (!fromDisk) return;

		if (!File.Exists(path)) {
			Remove(path);
			return;
		}

		if (InScope(path)) ParseDisk(path);
	}

	public void DocumentOpened(string path, string text) {
		lock (_sync) _open[path] = text;

		if (BrowsingServer) ThreadPool.QueueUserWorkItem(_ => TryParseAddon(path));

		Parse(path, text, keepOnError: true);
	}

	public void DocumentChanged(string path, string text) {
		lock (_sync) _open[path] = text;

		Schedule(path, ChangeDelayMilliseconds, fromDisk: false);
	}

	public void DocumentClosed(string path) {
		lock (_sync) {
			if (!_open.Remove(path)) return;
		}

		Schedule(path, 0, fromDisk: true);
	}

	public IReadOnlyList<string> OpenPaths() {
		lock (_sync) return [.. _open.Keys];
	}

	public string? OpenText(string path) {
		lock (_sync) return _open.GetValueOrDefault(path);
	}

	private void ParseDisk(string path) {
		string text;

		try {
			text = File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
			Remove(path);
			return;
		}

		Parse(path, text, keepOnError: false);
	}

	public ParsedFile? Parse(string path, string text, bool keepOnError) {
		ParsedFile? parsed;

		try {
			LuaNode chunk = LuaParser.Parse(text);
			parsed = TokenExtractor.Extract(path, chunk, _catalog, IsExcluded(path));
		}
		catch (LuaSyntaxException) {
			parsed = null;
		}

		bool counted;

		lock (_sync) {
			counted = _counted.Add(path);

			if (parsed is not null) _files[path] = parsed;
			else if (!keepOnError) _files.Remove(path);

			_version++;
			parsed ??= _files.GetValueOrDefault(path);
		}

		if (counted) CountChanged?.Invoke();

		FileParsed?.Invoke(path);
		return parsed;
	}

	private bool IsExcluded(string path) =>
		_excludedRoot is not null && path.StartsWith(_excludedRoot, StringComparison.OrdinalIgnoreCase);

	private void Remove(string path) {
		lock (_sync) {
			if (!_files.Remove(path)) return;

			_version++;
		}

		FileParsed?.Invoke(path);
	}

	public ParsedFile? Get(string path, string? text = null) {
		lock (_sync) {
			if (_files.TryGetValue(path, out ParsedFile? file)) return file;

			text ??= _open.GetValueOrDefault(path);
		}

		return text is null ? null : Parse(path, text, keepOnError: true);
	}

	public IReadOnlyList<ParsedFile> Files() {
		lock (_sync) return [.. _files.Values];
	}

	public FileTokenData Compiled {
		get {
			string? active = _activePath();

			lock (_sync) {
				if (_compiled is { } cached && cached.Version == _version && string.Equals(cached.Active, active, StringComparison.OrdinalIgnoreCase)) return cached.Data;

				FileTokenData data = new();
				ParsedFile? activeFile = null;

				foreach ((string path, ParsedFile file) in _files) {
					if (active is not null && string.Equals(path, active, StringComparison.OrdinalIgnoreCase)) activeFile = file;
					else data.Merge(file.Data);
				}

				if (activeFile is not null) data.Merge(activeFile.Data);

				_compiled = (_version, active, data);
				return data;
			}
		}
	}

	public void Dispose() {
		_disposed = true;
		_watcher?.Dispose();

		lock (_sync) {
			foreach (Timer timer in _pending.Values) timer.Dispose();

			_pending.Clear();
		}
	}
}
