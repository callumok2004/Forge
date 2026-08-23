let rpc = null;

function connect(connection) {
	rpc = connection;
}

function call(method, params) {
	return rpc.request(method, params);
}

function send(method, params) {
	rpc.notify(method, params);
}

class Disposable {
	constructor(dispose) {
		this._dispose = dispose ?? (() => {});
	}

	dispose() {
		this._dispose();
		this._dispose = () => {};
	}

	static from(...items) {
		return new Disposable(() => {
			for (const item of items) item?.dispose?.();
		});
	}
}

class EventEmitter {
	constructor() {
		this.listeners = new Set();
		this.event = listener => {
			this.listeners.add(listener);
			return new Disposable(() => this.listeners.delete(listener));
		};
	}

	fire(value) {
		for (const listener of [...this.listeners]) {
			try {
				listener(value);
			} catch (error) {
				send("host/log", { level: "error", message: String(error?.stack ?? error) });
			}
		}
	}

	dispose() {
		this.listeners.clear();
	}
}

const events = new Map();

function forgeEvent(name) {
	let emitter = events.get(name);
	if (!emitter) {
		emitter = new EventEmitter();
		events.set(name, emitter);
	}
	return emitter.event;
}

function dispatchEvent(name, value) {
	events.get(name)?.fire(value);
}

class Uri {
	constructor(scheme, authority, path, query, fragment) {
		this.scheme = scheme ?? "";
		this.authority = authority ?? "";
		this.path = path ?? "";
		this.query = query ?? "";
		this.fragment = fragment ?? "";
	}

	get fsPath() {
		const path = this.path.replace(/^\/([a-zA-Z]:)/, "$1");
		return path.replace(/\//g, "\\");
	}

	static file(path) {
		const normalised = path.replace(/\\/g, "/");
		return new Uri("file", "", normalised.startsWith("/") ? normalised : `/${normalised}`, "", "");
	}

	static parse(value) {
		try {
			const url = new URL(value);
			return new Uri(url.protocol.replace(/:$/, ""), url.host, url.pathname, url.search.replace(/^\?/, ""), url.hash.replace(/^#/, ""));
		} catch {
			return Uri.file(value);
		}
	}

	static from(parts) {
		return new Uri(parts.scheme, parts.authority, parts.path, parts.query, parts.fragment);
	}

	static joinPath(base, ...segments) {
		const path = [base.path.replace(/\/$/, ""), ...segments].join("/");
		return new Uri(base.scheme, base.authority, path, base.query, base.fragment);
	}

	with(parts) {
		return new Uri(parts.scheme ?? this.scheme, parts.authority ?? this.authority, parts.path ?? this.path, parts.query ?? this.query, parts.fragment ?? this.fragment);
	}

	toString() {
		return `${this.scheme}://${this.authority}${this.path}${this.query ? `?${this.query}` : ""}${this.fragment ? `#${this.fragment}` : ""}`;
	}

	toJSON() {
		return { $mid: "uri", scheme: this.scheme, authority: this.authority, path: this.path, query: this.query, fragment: this.fragment };
	}
}

class Position {
	constructor(line, character) {
		this.line = line;
		this.character = character;
	}
}

class Range {
	constructor(startLine, startCharacter, endLine, endCharacter) {
		if (startLine instanceof Position) {
			this.start = startLine;
			this.end = startCharacter;
		} else {
			this.start = new Position(startLine, startCharacter);
			this.end = new Position(endLine, endCharacter);
		}
	}

	get isEmpty() {
		return this.start.line === this.end.line && this.start.character === this.end.character;
	}
}

class Selection extends Range {
	constructor(anchorLine, anchorCharacter, activeLine, activeCharacter) {
		super(anchorLine, anchorCharacter, activeLine, activeCharacter);
		this.anchor = this.start;
		this.active = this.end;
	}
}

class FileSystemError extends Error {
	constructor(message, code) {
		super(message);
		this.code = code ?? "Unknown";
	}

	static FileNotFound(message) {
		return new FileSystemError(String(message ?? "file not found"), "FileNotFound");
	}

	static FileExists(message) {
		return new FileSystemError(String(message ?? "file exists"), "FileExists");
	}

	static FileNotADirectory(message) {
		return new FileSystemError(String(message ?? "not a directory"), "FileNotADirectory");
	}

	static FileIsADirectory(message) {
		return new FileSystemError(String(message ?? "is a directory"), "FileIsADirectory");
	}

	static NoPermissions(message) {
		return new FileSystemError(String(message ?? "no permissions"), "NoPermissions");
	}

	static Unavailable(message) {
		return new FileSystemError(String(message ?? "unavailable"), "Unavailable");
	}
}

const commandHandlers = new Map();
const webviewProviders = new Map();

const commands = {
	registerCommand(id, handler) {
		commandHandlers.set(id, handler);
		send("commands/register", { id });
		return new Disposable(() => {
			commandHandlers.delete(id);
			send("commands/unregister", { id });
		});
	},

	registerTextEditorCommand(id, handler) {
		return commands.registerCommand(id, handler);
	},

	executeCommand(id, ...args) {
		const local = commandHandlers.get(id);
		if (local) return Promise.resolve(local(...args));
		return call("commands/execute", { id, args });
	},

	getCommands() {
		return call("commands/list", {});
	},
};

const outputChannels = new Map();

const window = {
	get activeTextEditor() {
		return state.activeTextEditor;
	},

	get visibleTextEditors() {
		return state.visibleTextEditors;
	},

	get activeTerminal() {
		return state.activeTerminal;
	},

	tabGroups: {
		get all() {
			return state.tabGroups;
		},
		get activeTabGroup() {
			return state.tabGroups[0] ?? { tabs: [], activeTab: undefined };
		},
		onDidChangeTabs: forgeEvent("window.tabGroups.onDidChangeTabs"),
		onDidChangeTabGroups: forgeEvent("window.tabGroups.onDidChangeTabGroups"),
		close: tabs => call("window/closeTabs", { tabs }),
	},

	onDidChangeActiveTextEditor: forgeEvent("window.onDidChangeActiveTextEditor"),
	onDidChangeVisibleTextEditors: forgeEvent("window.onDidChangeVisibleTextEditors"),
	onDidChangeTextEditorSelection: forgeEvent("window.onDidChangeTextEditorSelection"),
	onDidChangeTextEditorVisibleRanges: forgeEvent("window.onDidChangeTextEditorVisibleRanges"),
	onDidChangeWindowState: forgeEvent("window.onDidChangeWindowState"),
	onDidChangeActiveColorTheme: forgeEvent("window.onDidChangeActiveColorTheme"),
	onDidOpenTerminal: forgeEvent("window.onDidOpenTerminal"),
	onDidCloseTerminal: forgeEvent("window.onDidCloseTerminal"),
	onDidChangeActiveTerminal: forgeEvent("window.onDidChangeActiveTerminal"),

	showErrorMessage: (message, ...items) => call("window/showMessage", { level: "error", message, items: items.flat() }),
	showWarningMessage: (message, ...items) => call("window/showMessage", { level: "warning", message, items: items.flat() }),
	showInformationMessage: (message, ...items) => call("window/showMessage", { level: "info", message, items: items.flat() }),
	showInputBox: options => call("window/showInputBox", { options: options ?? {} }),
	showTextDocument: (document, options) => call("window/showTextDocument", { uri: String(document?.uri ?? document), options: options ?? {} }),

	createOutputChannel(name, options) {
		let channel = outputChannels.get(name);
		if (channel) return channel;

		const line = (level, value, ...rest) => send("output/append", { name, value: `[${level}] ${value}${rest.length ? ` ${rest.map(r => typeof r === "string" ? r : JSON.stringify(r)).join(" ")}` : ""}
` });

		channel = {
			name,
			logLevel: 1,
			onDidChangeLogLevel: forgeEvent("output.onDidChangeLogLevel"),
			trace: (value, ...rest) => line("trace", value, ...rest),
			debug: (value, ...rest) => line("debug", value, ...rest),
			info: (value, ...rest) => line("info", value, ...rest),
			warn: (value, ...rest) => line("warn", value, ...rest),
			error: (value, ...rest) => line("error", value?.stack ?? value, ...rest),
			append: value => send("output/append", { name, value }),
			appendLine: value => send("output/append", { name, value: `${value}\n` }),
			clear: () => send("output/clear", { name }),
			show: () => send("output/show", { name }),
			hide: () => {},
			replace: value => {
				send("output/clear", { name });
				send("output/append", { name, value });
			},
			dispose: () => outputChannels.delete(name),
		};

		outputChannels.set(name, channel);
		return channel;
	},

	createStatusBarItem(alignment, priority) {
		const id = `status-${Math.random().toString(36).slice(2)}`;
		const item = {
			id,
			alignment,
			priority,
			text: "",
			tooltip: "",
			command: undefined,
			show: () => send("status/set", { id, text: item.text, command: item.command, alignment, priority }),
			hide: () => send("status/remove", { id }),
			dispose: () => send("status/remove", { id }),
		};
		return item;
	},

	registerWebviewViewProvider(viewId, provider) {
		webviewProviders.set(viewId, provider);
		send("webview/registerProvider", { viewId });
		return new Disposable(() => {
			webviewProviders.delete(viewId);
			send("webview/unregisterProvider", { viewId });
		});
	},

	registerWebviewPanelSerializer() {
		return new Disposable();
	},

	registerUriHandler() {
		return new Disposable();
	},

	createWebviewPanel(viewType, title) {
		return createWebview(`panel:${viewType}`, title, true);
	},

	withProgress(options, task) {
		const title = options?.title;

		if (title) send("window/progress", { title, state: "start" });

		const progress = { report: value => send("window/progress", { title, state: "report", message: value?.message }) };
		const token = { isCancellationRequested: false, onCancellationRequested: () => new Disposable() };

		return Promise.resolve(task(progress, token)).finally(() => {
			if (title) send("window/progress", { title, state: "end" });
		});
	},

	showQuickPick: (items, options) => call("window/showQuickPick", { items, options: options ?? {} }),
};

const workspace = {
	get workspaceFolders() {
		return state.workspaceFolders;
	},

	get textDocuments() {
		return state.textDocuments;
	},

	get name() {
		return state.workspaceFolders?.[0]?.name;
	},

	onDidChangeConfiguration: forgeEvent("workspace.onDidChangeConfiguration"),
	onDidCloseTextDocument: forgeEvent("workspace.onDidCloseTextDocument"),
	onDidSaveTextDocument: forgeEvent("workspace.onDidSaveTextDocument"),
	onWillSaveTextDocument: forgeEvent("workspace.onWillSaveTextDocument"),
	onDidChangeTextDocument: forgeEvent("workspace.onDidChangeTextDocument"),
	onDidOpenTextDocument: forgeEvent("workspace.onDidOpenTextDocument"),
	onDidChangeWorkspaceFolders: forgeEvent("workspace.onDidChangeWorkspaceFolders"),
	onDidCreateFiles: forgeEvent("workspace.onDidCreateFiles"),
	onDidDeleteFiles: forgeEvent("workspace.onDidDeleteFiles"),
	onDidRenameFiles: forgeEvent("workspace.onDidRenameFiles"),

	getConfiguration(section) {
		return {
			get: (key, fallback) => state.configuration[`${section ? `${section}.` : ""}${key}`] ?? fallback,
			has: key => `${section ? `${section}.` : ""}${key}` in state.configuration,
			update: (key, value) => call("workspace/updateConfiguration", { key: `${section ? `${section}.` : ""}${key}`, value }),
			inspect: () => undefined,
		};
	},

	asRelativePath(target) {
		const root = state.workspaceFolders?.[0]?.uri?.fsPath;
		const path = typeof target === "string" ? target : target?.fsPath ?? "";
		return root && path.startsWith(root) ? path.slice(root.length).replace(/^[\\/]/, "") : path;
	},

	openTextDocument: async target => {
		const options = target && typeof target === "object" && !target.fsPath ? target : {};

		const result = await call("workspace/openTextDocument", {
			target: typeof target === "string" ? target : target?.fsPath ?? options.path ?? null,
			content: options.content ?? null,
			language: options.language ?? null,
			fileName: options.fileName ?? null,
		});

		if (!result) return undefined;

		const uri = result.fsPath ? Uri.file(result.fsPath) : Uri.parse(String(result));

		return {
			uri,
			fileName: uri.fsPath,
			languageId: options.language ?? "plaintext",
			isUntitled: !result.fsPath,
			isDirty: false,
			lineCount: (options.content ?? "").split("\n").length,
			getText: () => options.content ?? "",
			save: () => Promise.resolve(true),
		};
	},
	registerFileSystemProvider: () => new Disposable(),
	registerTextDocumentContentProvider: () => new Disposable(),
	createFileSystemWatcher: () => ({
		onDidCreate: forgeEvent("noop"),
		onDidChange: forgeEvent("noop"),
		onDidDelete: forgeEvent("noop"),
		dispose: () => {},
	}),
};

const languages = {
	getDiagnostics: uri => call("languages/getDiagnostics", { uri: uri ? String(uri) : null }),
	onDidChangeDiagnostics: forgeEvent("languages.onDidChangeDiagnostics"),
	createDiagnosticCollection: () => ({ set: () => {}, delete: () => {}, clear: () => {}, dispose: () => {} }),
};

const env = {
	appName: "Forge",
	appHost: "desktop",
	uriScheme: "forge",
	language: "en",
	machineId: "forge",
	sessionId: `forge-${Date.now()}`,
	clipboard: {
		readText: () => call("env/clipboardRead", {}),
		writeText: value => call("env/clipboardWrite", { value }),
	},
	openExternal: target => call("env/openExternal", { target: String(target) }),
};

const extensions = {
	getExtension: () => undefined,
	all: [],
};

const webviews = new Map();

function webviewRelative(root, uri) {
	const target = (uri && (uri.fsPath ?? uri.path)) ?? String(uri ?? "");
	const normalised = target.split("\\").join("/").replace(/^\/(?=[a-zA-Z]:)/, "");

	if (!root) return normalised;

	const base = root.split("\\").join("/").replace(/\/+$/, "");

	return normalised.toLowerCase().startsWith(base.toLowerCase() + "/")
		? normalised.slice(base.length + 1)
		: normalised.replace(/^[a-zA-Z]:\//, "");
}

function createWebview(id, title, panel, root) {
	const messages = new EventEmitter();

	const webview = {
		options: {},
		html: "",
		cspSource: "https://forge-webview",
		onDidReceiveMessage: messages.event,
		postMessage: message => call("webview/postMessage", { id, message }),
		asWebviewUri: uri => Uri.parse(`https://forge-webview/${webviewRelative(root, uri)}`),
	};

	Object.defineProperty(webview, "html", {
		get: () => webview._html ?? "",
		set: value => {
			webview._html = value;
			send("webview/setHtml", { id, html: value });
		},
	});

	const view = {
		viewType: id,
		title,
		webview,
		visible: true,
		onDidDispose: forgeEvent(`webview.${id}.onDidDispose`),
		onDidChangeVisibility: forgeEvent(`webview.${id}.onDidChangeVisibility`),
		show: () => send("webview/show", { id }),
		reveal: () => send("webview/show", { id }),
		dispose: () => webviews.delete(id),
	};

	if (panel) {
		view.viewColumn = 1;
		view.active = true;
		view.options = {};
		view.onDidChangeViewState = forgeEvent(`webview.${id}.onDidChangeViewState`);
		view.reveal = column => {
			if (typeof column === "number") view.viewColumn = column;
			send("webview/show", { id });
		};
	}

	webviews.set(id, { view, messages });
	return view;
}

const state = {
	activeTextEditor: undefined,
	visibleTextEditors: [],
	activeTerminal: undefined,
	tabGroups: [],
	textDocuments: [],
	workspaceFolders: undefined,
	configuration: {},
};

const api = {
	version: "1.130.0",
	Disposable,
	EventEmitter,
	Uri,
	Position,
	Range,
	Selection,
	FileSystemError,
	commands,
	window,
	workspace,
	languages,
	env,
	extensions,
	ViewColumn: { Active: -1, Beside: -2, One: 1, Two: 2, Three: 3 },
	ProgressLocation: { SourceControl: 1, Window: 10, Notification: 15 },
	QuickPickItemKind: { Separator: -1, Default: 0 },
	UIKind: { Desktop: 1, Web: 2 },
	TextEditorRevealType: { Default: 0, InCenter: 1, InCenterIfOutsideViewport: 2, AtTop: 3 },
	StatusBarAlignment: { Left: 1, Right: 2 },
	DiagnosticSeverity: { Error: 0, Warning: 1, Information: 2, Hint: 3 },
	FileType: { Unknown: 0, File: 1, Directory: 2, SymbolicLink: 64 },
	FileChangeType: { Changed: 1, Created: 2, Deleted: 3 },
	ConfigurationTarget: { Global: 1, Workspace: 2, WorkspaceFolder: 3 },
	ExtensionMode: { Production: 1, Development: 2, Test: 3 },
	TabInputText: class TabInputText {},
	TabInputTextDiff: class TabInputTextDiff {},
	TabInputNotebook: class TabInputNotebook {},
	TabInputWebview: class TabInputWebview {},
	NotebookCellOutputItem: {
		text: (value, mime) => ({ mime: mime ?? "text/plain", data: value }),
		error: value => ({ mime: "application/vnd.code.notebook.error", data: { name: value?.name, message: value?.message, stack: value?.stack } }),
		stdout: value => ({ mime: "application/vnd.code.notebook.stdout", data: value }),
		stderr: value => ({ mime: "application/vnd.code.notebook.stderr", data: value }),
		json: value => ({ mime: "text/x-json", data: value }),
	},
	ThemeIcon: class ThemeIcon {
		constructor(id) {
			this.id = id;
		}
	},
	MarkdownString: class MarkdownString {
		constructor(value) {
			this.value = value ?? "";
			this.isTrusted = true;
		}
		appendMarkdown(value) {
			this.value += value;
			return this;
		}
	},
	CancellationTokenSource: class CancellationTokenSource {
		constructor() {
			const emitter = new EventEmitter();
			this.token = { isCancellationRequested: false, onCancellationRequested: emitter.event };
			this._emitter = emitter;
		}
		cancel() {
			this.token.isCancellationRequested = true;
			this._emitter.fire();
		}
		dispose() {
			this._emitter.dispose();
		}
	},
};

module.exports = api;
module.exports.__forge = { connect, state, dispatchEvent, commandHandlers, webviewProviders, webviews, createWebview };
