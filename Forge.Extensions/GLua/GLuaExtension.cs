using Forge.Common.Commands;
using Forge.Common.Extensions;
using Forge.Extensions.GLua.Completion;
using Forge.Extensions.GLua.Index;
using Forge.Extensions.GLua.Navigation;
using Forge.Extensions.GLua.Signatures;
using Forge.Extensions.GLua.Wiki;
using Forge.Extensions.Host;
using Forge.Theming;

namespace Forge.Extensions.GLua;

public sealed class GLuaExtension : ForgeExtension
{
	private const string StatusId = "glua.parsed";

	public override string Identifier => "forge.glua";

	public override string DisplayName => "GLua Enhanced";

	public override string Description => "Garry's Mod Lua: wiki completions, hovers, globals tools and bytecode heatmap";

	public override ActivationEvent Activation => ActivationEvent.FileOpened | ActivationEvent.CommandInvoked;

	protected override string CommandPrefix => "glua-enhanced.";

	public static string Directory => Path.Combine(AppContext.BaseDirectory, "Extensions", "GLua");

	public static string TempRoot => Path.Combine(Path.GetTempPath(), "glua-enhanced");

	private GLuaIndex? _index;
	private ICommandHost? _host;

	internal GLuaCatalog? Catalog { get; private set; }

	internal GLuaIndex? Index => _index;

	internal ICommandHost? Host => _host;

	private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal Task Ready => _ready.Task;

	public override bool ActivatesIn(string? workspacePath) => false;

	public override bool ActivatesFor(string filePath) => filePath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);

	public override void Register(ICommandRegistry commands) => GLuaCommands.Register(commands, this);

	protected override void OnActivate(IExtensionContext context) {
		ICommandHost host = context.GetHost();
		_host = host;

		WikiMarkdown.IconDirectory = Path.Combine(Directory, "resources", "icons");

		_ = Task.Run(() => {
			try {
				WikiData wiki = WikiReader.Load(Path.Combine(Directory, "resources", "wiki.json"));
				GLuaCatalog catalog = GLuaCatalog.Build(wiki, Path.Combine(Directory, "resources", "sounds.json"));
				GLuaIndex index = new(catalog, host.GetActiveFilePath, TempRoot);
				GLuaSignatures signatures = new(catalog, index);
				GLuaCompletions completions = new(catalog, index, signatures, host.GetWorkspacePath);
				GLuaNavigation navigation = new(catalog, index, TempRoot, host);

				Catalog = catalog;
				_index = index;

				host.ObserveDocuments((kind, path, text) => {
					if (!path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) return;

					switch (kind) {
						case DocumentEvent.Opened when text is not null:
							index.DocumentOpened(path, text);
							break;
						case DocumentEvent.Changed when text is not null:
							ClearHeatmap(path);
							index.DocumentChanged(path, text);
							break;
						case DocumentEvent.Closed:
							index.DocumentClosed(path);
							break;
					}
				});

				index.CountChanged += () => UpdateStatus(host, index);
				host.ProvideLanguageFeatures(new GLuaFeatures(this, catalog, completions, signatures, navigation, host));

				if (host.GetActiveFilePath() is { } active && ActivatesFor(active) && host.GetDocumentText(active) is { } activeText) index.DocumentOpened(active, activeText);

				_ready.TrySetResult();
				index.Start(host.GetWorkspacePath(), host.GetActiveFilePath() is { } open ? [open] : []);
				UpdateStatus(host, index);
				host.Refresh();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
				host.Channel("GLua").Append("GLua failed to load: " + exception.Message);
				_ready.TrySetException(exception);
			}
		});
	}

	private static void UpdateStatus(ICommandHost host, GLuaIndex index) {
		int count = index.ParsedCount;
		host.SetStatusItem(StatusId, "search", "Parsed " + count + " GLua file" + (count == 1 ? "" : "s"), null);
	}

	private readonly Dictionary<string, (IReadOnlyDictionary<int, Color> Backgrounds, IReadOnlyDictionary<int, string> Hovers)> _heatmaps = [with(StringComparer.OrdinalIgnoreCase)];
	private readonly Lock _heatmapSync = new();

	internal void SetHeatmap(string path, IReadOnlyDictionary<int, Color> backgrounds, IReadOnlyDictionary<int, string> hovers) {
		lock (_heatmapSync) _heatmaps[path] = (backgrounds, hovers);
	}

	internal void ClearHeatmap(string path) {
		lock (_heatmapSync) _heatmaps.Remove(path);
	}

	internal IReadOnlyDictionary<int, Color>? Heatmap(string path) {
		lock (_heatmapSync) return _heatmaps.TryGetValue(path, out var heatmap) ? heatmap.Backgrounds : null;
	}

	internal string? HeatmapHover(string path, int line) {
		lock (_heatmapSync) return _heatmaps.TryGetValue(path, out var heatmap) && heatmap.Hovers.TryGetValue(line, out string? hover) ? hover : null;
	}

	public override void Deactivate() {
		_index?.Dispose();
		_index = null;
		_host?.RemoveStatusItem(StatusId);
	}
}
