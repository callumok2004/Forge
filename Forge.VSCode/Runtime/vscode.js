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

function log(level, message) {
	send("host/log", { level, message: String(message) });
}

class Disposable {
	constructor(dispose) {
		this._dispose = dispose ?? (() => {});
	}

	dispose() {
		const dispose = this._dispose;
		this._dispose = () => {};
		return dispose();
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
		this.event = (listener, thisArg, disposables) => {
			const bound = thisArg ? listener.bind(thisArg) : listener;
			this.listeners.add(bound);
			const disposable = new Disposable(() => this.listeners.delete(bound));
			if (Array.isArray(disposables)) disposables.push(disposable);
			return disposable;
		};
	}

	fire(value) {
		for (const listener of [...this.listeners]) {
			try {
				listener(value);
			} catch (error) {
				log("error", error?.stack ?? error);
			}
		}
	}

	dispose() {
		this.listeners.clear();
	}
}

const emitters = new Map();

function emitter(name) {
	let existing = emitters.get(name);
	if (!existing) {
		existing = new EventEmitter();
		emitters.set(name, existing);
	}
	return existing;
}

function forgeEvent(name) {
	return emitter(name).event;
}

function dispatchEvent(name, value) {
	emitters.get(name)?.fire(value);
}

const noopEvent = () => new Disposable();

class Uri {
	constructor(scheme, authority, path, query, fragment) {
		this.scheme = scheme || "file";
		this.authority = authority ?? "";
		this.path = path ?? "";
		this.query = query ?? "";
		this.fragment = fragment ?? "";
	}

	get fsPath() {
		if (this.authority && this.path.length > 1 && this.scheme === "file") return `\\\\${this.authority}${this.path.replace(/\//g, "\\")}`;

		const path = /^\/[a-zA-Z]:/.test(this.path) ? this.path.slice(1) : this.path;
		return path.replace(/\//g, "\\");
	}

	static file(path) {
		let normalised = String(path).replace(/\\/g, "/");
		let authority = "";

		if (normalised.startsWith("//")) {
			const end = normalised.indexOf("/", 2);
			authority = end < 0 ? normalised.slice(2) : normalised.slice(2, end);
			normalised = end < 0 ? "/" : normalised.slice(end);
		}

		if (!normalised.startsWith("/")) normalised = `/${normalised}`;

		return new Uri("file", authority, normalised, "", "");
	}

	static parse(value) {
		const text = String(value);
		const match = /^([a-zA-Z][\w+.-]*):(?:\/\/([^/?#]*))?([^?#]*)(?:\?([^#]*))?(?:#(.*))?$/.exec(text);

		if (!match || /^[a-zA-Z]:[\\/]/.test(text)) return Uri.file(text);

		const decode = part => {
			try {
				return decodeURIComponent(part ?? "");
			} catch {
				return part ?? "";
			}
		};

		return new Uri(match[1], decode(match[2]), decode(match[3]), decode(match[4]), decode(match[5]));
	}

	static from(parts) {
		return new Uri(parts.scheme, parts.authority, parts.path, parts.query, parts.fragment);
	}

	static joinPath(base, ...segments) {
		const joined = [base.path.replace(/\/+$/, ""), ...segments.map(segment => String(segment).replace(/\\/g, "/").replace(/^\/+|\/+$/g, ""))].join("/");
		const parts = [];

		for (const part of joined.split("/")) {
			if (part === "..") parts.pop();
			else if (part !== ".") parts.push(part);
		}

		return new Uri(base.scheme, base.authority, parts.join("/") || "/", base.query, base.fragment);
	}

	static isUri(value) {
		return value instanceof Uri;
	}

	static revive(value) {
		if (!value) return value;
		if (value instanceof Uri) return value;
		if (typeof value === "string") return Uri.parse(value);
		return new Uri(value.scheme, value.authority, value.path, value.query, value.fragment);
	}

	with(parts) {
		return new Uri(parts.scheme ?? this.scheme, parts.authority ?? this.authority, parts.path ?? this.path, parts.query ?? this.query, parts.fragment ?? this.fragment);
	}

	toString(skipEncoding) {
		if (skipEncoding) {
			const hasAuthority = this.authority || this.scheme === "file";
			return `${this.scheme}:${hasAuthority ? `//${this.authority}` : ""}${this.path}${this.query ? `?${this.query}` : ""}${this.fragment ? `#${this.fragment}` : ""}`;
		}

		const encodePath = path => path.split("/").map(segment => encodeURIComponent(segment)).join("/");
		const hasAuthority = this.authority || this.scheme === "file";

		return `${this.scheme}:${hasAuthority ? `//${encodeURIComponent(this.authority)}` : ""}${encodePath(this.path)}${this.query ? `?${encodeURIComponent(this.query)}` : ""}${this.fragment ? `#${encodeURIComponent(this.fragment)}` : ""}`;
	}

	toJSON() {
		return { $mid: 1, scheme: this.scheme, authority: this.authority, path: this.path, query: this.query, fragment: this.fragment, fsPath: this.scheme === "file" ? this.fsPath : undefined, external: this.toString() };
	}
}

function uriKey(uri) {
	return Uri.revive(uri).toString();
}

class Position {
	constructor(line, character) {
		this.line = Math.max(0, line | 0);
		this.character = Math.max(0, character | 0);
	}

	isBefore(other) {
		return this.line < other.line || (this.line === other.line && this.character < other.character);
	}

	isBeforeOrEqual(other) {
		return this.isBefore(other) || this.isEqual(other);
	}

	isAfter(other) {
		return !this.isBeforeOrEqual(other);
	}

	isAfterOrEqual(other) {
		return !this.isBefore(other);
	}

	isEqual(other) {
		return this.line === other.line && this.character === other.character;
	}

	compareTo(other) {
		return this.isBefore(other) ? -1 : this.isEqual(other) ? 0 : 1;
	}

	translate(lineDelta = 0, characterDelta = 0) {
		if (typeof lineDelta === "object") return new Position(this.line + (lineDelta.lineDelta ?? 0), this.character + (lineDelta.characterDelta ?? 0));
		return new Position(this.line + lineDelta, this.character + characterDelta);
	}

	with(line, character) {
		if (typeof line === "object") return new Position(line.line ?? this.line, line.character ?? this.character);
		return new Position(line ?? this.line, character ?? this.character);
	}
}

function position(value) {
	return value instanceof Position ? value : new Position(value?.line ?? 0, value?.character ?? 0);
}

class Range {
	constructor(startLine, startCharacter, endLine, endCharacter) {
		let start;
		let end;

		if (typeof startLine === "object") {
			start = position(startLine);
			end = position(startCharacter);
		} else {
			start = new Position(startLine, startCharacter);
			end = new Position(endLine, endCharacter);
		}

		if (end.isBefore(start)) [start, end] = [end, start];

		this.start = start;
		this.end = end;
	}

	get isEmpty() {
		return this.start.isEqual(this.end);
	}

	get isSingleLine() {
		return this.start.line === this.end.line;
	}

	contains(value) {
		if (value instanceof Range) return this.contains(value.start) && this.contains(value.end);
		const point = position(value);
		return !point.isBefore(this.start) && !this.end.isBefore(point);
	}

	isEqual(other) {
		return this.start.isEqual(other.start) && this.end.isEqual(other.end);
	}

	intersection(other) {
		const start = this.start.isAfter(other.start) ? this.start : other.start;
		const end = this.end.isBefore(other.end) ? this.end : other.end;
		return start.isAfter(end) ? undefined : new Range(start, end);
	}

	union(other) {
		return new Range(this.start.isBefore(other.start) ? this.start : other.start, this.end.isAfter(other.end) ? this.end : other.end);
	}

	with(start, end) {
		if (start && typeof start === "object" && !(start instanceof Position)) return new Range(start.start ?? this.start, start.end ?? this.end);
		return new Range(start ?? this.start, end ?? this.end);
	}
}

function range(value) {
	if (!value) return undefined;
	return value instanceof Range ? value : new Range(position(value.start), position(value.end));
}

class Selection extends Range {
	constructor(anchorLine, anchorCharacter, activeLine, activeCharacter) {
		let anchor;
		let active;

		if (typeof anchorLine === "object") {
			anchor = position(anchorLine);
			active = position(anchorCharacter);
		} else {
			anchor = new Position(anchorLine, anchorCharacter);
			active = new Position(activeLine, activeCharacter);
		}

		super(anchor, active);
		this.anchor = anchor;
		this.active = active;
	}

	get isReversed() {
		return this.active.isBefore(this.anchor);
	}
}

function selection(value) {
	if (!value) return new Selection(0, 0, 0, 0);
	if (value instanceof Selection) return value;
	return new Selection(position(value.anchor ?? value.start), position(value.active ?? value.end));
}

class Location {
	constructor(uri, rangeOrPosition) {
		this.uri = uri;
		this.range = rangeOrPosition instanceof Position ? new Range(rangeOrPosition, rangeOrPosition) : rangeOrPosition;
	}
}

class TextEdit {
	constructor(editRange, newText) {
		this.range = editRange;
		this.newText = newText;
	}

	static replace(editRange, newText) {
		return new TextEdit(editRange, newText);
	}

	static insert(at, newText) {
		return new TextEdit(new Range(at, at), newText);
	}

	static delete(editRange) {
		return new TextEdit(editRange, "");
	}
}

class WorkspaceEdit {
	constructor() {
		this._edits = new Map();
		this._files = [];
	}

	get size() {
		return this._edits.size + this._files.length;
	}

	_bucket(uri) {
		const key = uriKey(uri);
		let bucket = this._edits.get(key);
		if (!bucket) {
			bucket = { uri: Uri.revive(uri), edits: [] };
			this._edits.set(key, bucket);
		}
		return bucket.edits;
	}

	replace(uri, editRange, newText) {
		this._bucket(uri).push(new TextEdit(editRange, newText));
	}

	insert(uri, at, newText) {
		this._bucket(uri).push(TextEdit.insert(at, newText));
	}

	delete(uri, editRange) {
		this._bucket(uri).push(TextEdit.delete(editRange));
	}

	has(uri) {
		return this._edits.has(uriKey(uri));
	}

	set(uri, edits) {
		const bucket = this._bucket(uri);
		bucket.length = 0;
		for (const edit of edits ?? []) bucket.push(Array.isArray(edit) ? edit[0] : edit);
	}

	get(uri) {
		return this._edits.get(uriKey(uri))?.edits ?? [];
	}

	entries() {
		return [...this._edits.values()].map(bucket => [bucket.uri, bucket.edits]);
	}

	createFile(uri, options) {
		this._files.push({ kind: "create", uri: Uri.revive(uri), options: options ?? {} });
	}

	deleteFile(uri, options) {
		this._files.push({ kind: "delete", uri: Uri.revive(uri), options: options ?? {} });
	}

	renameFile(oldUri, newUri, options) {
		this._files.push({ kind: "rename", uri: Uri.revive(oldUri), target: Uri.revive(newUri), options: options ?? {} });
	}
}

const EndOfLine = { LF: 1, CRLF: 2 };
const TextDocumentChangeReason = { Undo: 1, Redo: 2 };
const TextDocumentSaveReason = { Manual: 1, AfterDelay: 2, FocusOut: 3 };

class TextLine {
	constructor(lineNumber, text, isLast) {
		this.lineNumber = lineNumber;
		this.text = text;
		this.range = new Range(lineNumber, 0, lineNumber, text.length);
		this.rangeIncludingLineBreak = isLast ? this.range : new Range(lineNumber, 0, lineNumber + 1, 0);
		let first = 0;
		while (first < text.length && (text[first] === " " || text[first] === "\t")) first++;
		this.firstNonWhitespaceCharacterIndex = first;
		this.isEmptyOrWhitespace = first === text.length;
	}
}

const documents = new Map();

class TextDocument {
	constructor(uri, text, languageId, version) {
		this.uri = Uri.revive(uri);
		this.languageId = languageId || languageFor(this.uri.path);
		this.version = version ?? 1;
		this.isDirty = false;
		this.isClosed = false;
		this.encoding = "utf8";
		this._setText(text ?? "");
	}

	get fileName() {
		return this.uri.scheme === "file" ? this.uri.fsPath : this.uri.path;
	}

	get isUntitled() {
		return this.uri.scheme === "untitled";
	}

	get eol() {
		return this._text.includes("\r\n") ? EndOfLine.CRLF : EndOfLine.LF;
	}

	get lineCount() {
		return this._lineStarts.length;
	}

	_setText(text) {
		this._text = text;
		const starts = [0];
		for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) starts.push(i + 1);
		this._lineStarts = starts;
	}

	_lineEnd(line) {
		const next = line + 1 < this._lineStarts.length ? this._lineStarts[line + 1] - 1 : this._text.length;
		return next > this._lineStarts[line] && this._text.charCodeAt(next - 1) === 13 && line + 1 < this._lineStarts.length ? next - 1 : next;
	}

	offsetAt(at) {
		const point = this.validatePosition(position(at));
		return this._lineStarts[point.line] + point.character;
	}

	positionAt(offset) {
		const clamped = Math.max(0, Math.min(offset | 0, this._text.length));
		let low = 0;
		let high = this._lineStarts.length - 1;

		while (low < high) {
			const middle = (low + high + 1) >> 1;
			if (this._lineStarts[middle] <= clamped) low = middle;
			else high = middle - 1;
		}

		return new Position(low, clamped - this._lineStarts[low]);
	}

	getText(within) {
		if (!within) return this._text;
		const target = this.validateRange(range(within));
		return this._text.slice(this.offsetAt(target.start), this.offsetAt(target.end));
	}

	lineAt(lineOrPosition) {
		const line = typeof lineOrPosition === "number" ? lineOrPosition : lineOrPosition.line;
		if (line < 0 || line >= this._lineStarts.length) throw new Error("Illegal value for `line`");
		return new TextLine(line, this._text.slice(this._lineStarts[line], this._lineEnd(line)), line === this._lineStarts.length - 1);
	}

	validatePosition(at) {
		const point = position(at);
		if (point.line >= this._lineStarts.length) {
			const last = this._lineStarts.length - 1;
			return new Position(last, this._lineEnd(last) - this._lineStarts[last]);
		}
		const length = this._lineEnd(point.line) - this._lineStarts[point.line];
		return point.character > length ? new Position(point.line, length) : point;
	}

	validateRange(within) {
		return new Range(this.validatePosition(within.start), this.validatePosition(within.end));
	}

	getWordRangeAtPosition(at, pattern) {
		const point = this.validatePosition(at);
		const text = this.lineAt(point.line).text;
		const regex = pattern ? new RegExp(pattern.source, pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`) : /[\w$]+/g;
		let match;

		while ((match = regex.exec(text))) {
			const start = match.index;
			const end = start + match[0].length;
			if (start <= point.character && point.character <= end) return new Range(point.line, start, point.line, end);
			if (match[0].length === 0) regex.lastIndex++;
		}

		return undefined;
	}

	_applyChanges(changes, version, reason) {
		const contentChanges = [];

		for (const change of changes) {
			const start = this.positionAt(change.offset);
			const end = this.positionAt(change.offset + change.length);
			contentChanges.push({ range: new Range(start, end), rangeOffset: change.offset, rangeLength: change.length, text: change.text });
			this._setText(this._text.slice(0, change.offset) + change.text + this._text.slice(change.offset + change.length));
		}

		this.version = version ?? this.version + 1;
		return { document: this, contentChanges, reason };
	}

	async save() {
		if (this.isClosed) return false;

		if (this.uri.scheme === "file") {
			const saved = await call("documents/save", { uri: this.uri.toJSON() });
			if (saved) this.isDirty = false;
			return saved === true;
		}

		const provider = fileSystemProviders.get(this.uri.scheme);

		if (!provider?.writeFile) return false;

		emitter("workspace.onWillSaveTextDocument").fire(willSaveEvent(this, TextDocumentSaveReason.Manual));
		await provider.writeFile(this.uri, Buffer.from(this._text, "utf8"), { create: true, overwrite: true });
		this.isDirty = false;
		send("documents/dirty", { uri: this.uri.toJSON(), isDirty: false });
		emitter("workspace.onDidSaveTextDocument").fire(this);
		return true;
	}
}

function willSaveEvent(document, reason) {
	const pending = [];
	return {
		document,
		reason,
		waitUntil: thenable => pending.push(thenable),
		_pending: pending,
	};
}

const languageIds = {
	cs: "csharp", csx: "csharp", js: "javascript", mjs: "javascript", cjs: "javascript", jsx: "javascriptreact", ts: "typescript", mts: "typescript", cts: "typescript", tsx: "typescriptreact",
	json: "json", jsonc: "jsonc", md: "markdown", py: "python", rs: "rust", go: "go", java: "java", c: "c", h: "c", cpp: "cpp", cc: "cpp", hpp: "cpp", css: "css", scss: "scss", less: "less",
	html: "html", htm: "html", xml: "xml", csproj: "xml", props: "xml", targets: "xml", slnx: "xml", yml: "yaml", yaml: "yaml", sh: "shellscript", bash: "shellscript", ps1: "powershell",
	bat: "bat", cmd: "bat", sql: "sql", php: "php", rb: "ruby", lua: "lua", swift: "swift", kt: "kotlin", dart: "dart", fs: "fsharp", vb: "vb", toml: "toml", ini: "ini", txt: "plaintext",
};

function languageFor(path) {
	const name = String(path ?? "").split("/").pop() ?? "";
	const dot = name.lastIndexOf(".");
	if (dot < 0) return name.toLowerCase() === "dockerfile" ? "dockerfile" : "plaintext";
	return languageIds[name.slice(dot + 1).toLowerCase()] ?? "plaintext";
}

function trackDocument(document) {
	documents.set(document.uri.toString(), document);
	return document;
}

function findDocument(uri) {
	return documents.get(uriKey(uri));
}

const fileSystemProviders = new Map();
const contentProviders = new Map();

async function readVirtual(uri) {
	const target = Uri.revive(uri);
	const contentProvider = contentProviders.get(target.scheme);

	if (contentProvider) {
		const text = await contentProvider.provideTextDocumentContent(target, { isCancellationRequested: false, onCancellationRequested: noopEvent });
		return text ?? "";
	}

	const provider = fileSystemProviders.get(target.scheme);

	if (provider) return Buffer.from(await provider.readFile(target)).toString("utf8");

	throw FileSystemError.Unavailable(target);
}

async function openDocument(target) {
	const uri = Uri.revive(target);
	const existing = findDocument(uri);

	if (existing && !existing.isClosed) return existing;

	if (uri.scheme === "file") {
		const result = await call("documents/open", { uri: uri.toJSON() });
		if (!result) throw FileSystemError.FileNotFound(uri);

		const document = trackDocument(new TextDocument(uri, result.text, result.languageId, result.version));
		document.isDirty = result.isDirty === true;
		emitter("workspace.onDidOpenTextDocument").fire(document);
		return document;
	}

	const document = trackDocument(new TextDocument(uri, await readVirtual(uri)));
	send("documents/virtualOpened", { uri: uri.toJSON(), text: document.getText(), languageId: document.languageId });
	emitter("workspace.onDidOpenTextDocument").fire(document);
	return document;
}

const documentHandlers = {
	"documents/opened": params => {
		const uri = Uri.revive(params.uri);
		const key = uri.toString();
		const existing = documents.get(key);

		if (existing && !existing.isClosed) {
			if (existing.getText() !== params.text) existing._setText(params.text);
			existing.version = params.version ?? existing.version;
			existing.isDirty = params.isDirty === true;
			return null;
		}

		const document = trackDocument(new TextDocument(uri, params.text, params.languageId, params.version));
		document.isDirty = params.isDirty === true;
		emitter("workspace.onDidOpenTextDocument").fire(document);
		return null;
	},

	"documents/changed": params => {
		const document = findDocument(params.uri);
		if (!document) return null;

		const event = document._applyChanges(params.changes ?? [], params.version, params.reason);
		document.isDirty = params.isDirty ?? true;
		emitter("workspace.onDidChangeTextDocument").fire(event);
		return null;
	},

	"documents/closed": params => {
		const key = uriKey(params.uri);
		const document = documents.get(key);
		if (!document) return null;

		document.isClosed = true;
		documents.delete(key);
		emitter("workspace.onDidCloseTextDocument").fire(document);
		return null;
	},

	"documents/willSave": async params => {
		const document = findDocument(params.uri);
		if (!document) return null;

		const event = willSaveEvent(document, params.reason ?? TextDocumentSaveReason.Manual);
		emitter("workspace.onWillSaveTextDocument").fire(event);
		await Promise.allSettled(event._pending);
		return null;
	},

	"documents/saved": params => {
		const document = findDocument(params.uri);
		if (!document) return null;

		document.isDirty = false;
		emitter("workspace.onDidSaveTextDocument").fire(document);
		return null;
	},

	"documents/read": async params => ({ text: await readVirtual(params.uri) }),

	"documents/write": async params => {
		const uri = Uri.revive(params.uri);
		const document = findDocument(uri);

		if (document) {
			const event = willSaveEvent(document, TextDocumentSaveReason.Manual);
			emitter("workspace.onWillSaveTextDocument").fire(event);
			await Promise.allSettled(event._pending);
		}

		const provider = fileSystemProviders.get(uri.scheme);
		if (provider?.writeFile) await provider.writeFile(uri, Buffer.from(params.text, "utf8"), { create: true, overwrite: true });

		if (document) {
			document.isDirty = false;
			emitter("workspace.onDidSaveTextDocument").fire(document);
		}

		return true;
	},

	"terminal/closed": params => {
		const terminal = terminals.get(params.id);
		if (!terminal) return null;

		terminal.exitStatus = { code: params.code, reason: 2 };
		terminals.delete(params.id);
		emitter("window.onDidCloseTerminal").fire(terminal);
		return null;
	},

	"terminal/active": params => {
		state.activeTerminalId = params.id ?? null;
		emitter("window.onDidChangeActiveTerminal").fire(window.activeTerminal);
		return null;
	},
};

const commandHandlers = new Map();
const webviewProviders = new Map();

function toPlain(value) {
	if (value === undefined || value === null) return value ?? null;
	if (value instanceof Uri) return value.toJSON();
	if (Array.isArray(value)) return value.map(toPlain);
	if (typeof value === "object") {
		if (typeof value.toJSON === "function") return value.toJSON();
		const plain = {};
		for (const [key, inner] of Object.entries(value)) if (typeof inner !== "function") plain[key] = toPlain(inner);
		return plain;
	}
	return value;
}

async function openDiff(original, modified, title, options) {
	const left = Uri.revive(original);
	const right = Uri.revive(modified);
	const leftDocument = await openDocument(left);
	const rightDocument = await openDocument(right);

	await call("window/openDiff", {
		original: left.toJSON(),
		modified: right.toJSON(),
		originalText: leftDocument.getText(),
		modifiedText: rightDocument.getText(),
		modifiedWritable: right.scheme === "file" || typeof fileSystemProviders.get(right.scheme)?.writeFile === "function",
		title: title ?? "",
		options: toPlain(options ?? {}),
	});

	return undefined;
}

const builtInCommands = {
	"vscode.diff": (original, modified, title, options) => openDiff(original, modified, title, options),
	"vscode.changes": async (title, resources) => {
		for (const [, original, modified] of resources ?? []) await openDiff(original, modified, title);
	},
	"vscode.open": async (target, options) => {
		await window.showTextDocument(Uri.revive(target), typeof options === "object" ? options : { viewColumn: options });
	},
	"vscode.openFolder": target => call("commands/execute", { id: "vscode.openFolder", args: [toPlain(target)] }),
	"workbench.action.openSettings": query => call("commands/execute", { id: "workbench.action.openSettings", args: [query ?? null] }),
};

const commands = {
	registerCommand(id, handler, thisArg) {
		const bound = thisArg ? handler.bind(thisArg) : handler;
		commandHandlers.set(id, bound);
		send("commands/register", { id });
		return new Disposable(() => {
			commandHandlers.delete(id);
			send("commands/unregister", { id });
		});
	},

	registerTextEditorCommand(id, handler, thisArg) {
		return commands.registerCommand(id, (...args) => {
			const editor = window.activeTextEditor;
			if (!editor) return undefined;
			return handler.call(thisArg, editor, { replace() {}, insert() {}, delete() {}, setEndOfLine() {} }, ...args);
		});
	},

	async executeCommand(id, ...args) {
		const local = commandHandlers.get(id);
		if (local) return local(...args);

		const builtIn = builtInCommands[id];
		if (builtIn) return builtIn(...args);

		return call("commands/execute", { id, args: toPlain(args) });
	},

	getCommands(filterInternal) {
		return call("commands/list", { filterInternal: filterInternal === true }).then(list => [...new Set([...(list ?? []), ...commandHandlers.keys(), ...Object.keys(builtInCommands)])]);
	},
};

class TextEditor {
	constructor(document, source) {
		this.document = document;
		this._update(source);
	}

	_update(source) {
		const selections = (source?.selections ?? (source?.selection ? [source.selection] : [])).map(selection);
		this.selections = selections.length ? selections : [new Selection(0, 0, 0, 0)];
		this.visibleRanges = (source?.visibleRanges ?? []).map(range);
		this.viewColumn = source?.viewColumn ?? 1;
		this.options = { tabSize: source?.tabSize ?? 4, insertSpaces: source?.insertSpaces ?? false, cursorStyle: 1, lineNumbers: 1 };
	}

	get selection() {
		return this.selections[0];
	}

	set selection(value) {
		this.selections = [selection(value)];
		send("editor/setSelection", { uri: this.document.uri.toJSON(), selections: this.selections.map(toPlain) });
	}

	async edit(callback) {
		const edit = new WorkspaceEdit();
		const builder = {
			replace: (location, value) => edit.replace(this.document.uri, location instanceof Position ? new Range(location, location) : location, value),
			insert: (location, value) => edit.insert(this.document.uri, location, value),
			delete: location => edit.delete(this.document.uri, location),
			setEndOfLine: () => {},
		};
		callback(builder);
		return workspace.applyEdit(edit);
	}

	insertSnippet(snippet, location) {
		const value = typeof snippet === "string" ? snippet : snippet?.value ?? "";
		const target = location instanceof Range ? location : location instanceof Position ? new Range(location, location) : this.selection;
		return this.edit(builder => builder.replace(target, value.replace(/\$\{\d+:?([^}]*)\}|\$\d+/g, "$1")));
	}

	revealRange(target, revealType) {
		send("editor/reveal", { uri: this.document.uri.toJSON(), range: toPlain(target), revealType: revealType ?? 0 });
	}

	setDecorations(type, ranges) {
		send("editor/setDecorations", { uri: this.document.uri.toJSON(), key: type?.key, ranges: (ranges ?? []).map(entry => toPlain(entry?.range ?? entry)) });
	}

	show(column) {
		window.showTextDocument(this.document, { viewColumn: column });
	}

	hide() {}
}

const editorCache = new Map();

function editorFor(source) {
	if (!source?.document?.uri) return undefined;

	const uri = Uri.revive(source.document.uri);
	const document = findDocument(uri) ?? trackDocument(new TextDocument(uri, source.document.text ?? "", source.document.languageId));
	const key = uri.toString();
	let editor = editorCache.get(key);

	if (!editor || editor.document !== document) {
		editor = new TextEditor(document, source);
		editorCache.set(key, editor);
	} else {
		editor._update(source);
	}

	return editor;
}

class TabInputText {
	constructor(uri) {
		this.uri = uri;
	}
}

class TabInputTextDiff {
	constructor(original, modified) {
		this.original = original;
		this.modified = modified;
	}
}

class TabInputCustom {
	constructor(uri, viewType) {
		this.uri = uri;
		this.viewType = viewType;
	}
}

class TabInputWebview {
	constructor(viewType) {
		this.viewType = viewType;
	}
}

class TabInputNotebook {
	constructor(uri, notebookType) {
		this.uri = uri;
		this.notebookType = notebookType;
	}
}

class TabInputNotebookDiff {
	constructor(original, modified, notebookType) {
		this.original = original;
		this.modified = modified;
		this.notebookType = notebookType;
	}
}

class TabInputTerminal {}

function tabInput(input) {
	switch (input?.kind) {
		case "text": return new TabInputText(Uri.revive(input.uri));
		case "diff": return new TabInputTextDiff(Uri.revive(input.original), Uri.revive(input.modified));
		case "webview": return new TabInputWebview(input.viewType ?? "");
		case "terminal": return new TabInputTerminal();
		default: return undefined;
	}
}

let tabGroupsCache = [];

function buildTabGroups(raw) {
	const groups = (raw ?? []).map((group, index) => {
		const tabGroup = { viewColumn: group.viewColumn ?? index + 1, isActive: group.isActive === true, tabs: [], activeTab: undefined };
		tabGroup.tabs = (group.tabs ?? []).map(tab => ({
			label: tab.label ?? "",
			input: tabInput(tab.input),
			isActive: tab.isActive === true,
			isDirty: tab.isDirty === true,
			isPinned: tab.isPinned === true,
			isPreview: tab.isPreview === true,
			group: tabGroup,
			_id: tab.id,
		}));
		tabGroup.activeTab = tabGroup.tabs.find(tab => tab.isActive);
		return tabGroup;
	});

	tabGroupsCache = groups;
	return groups;
}

const terminals = new Map();

class Terminal {
	constructor(id, name, options) {
		this._id = id;
		this.name = name;
		this.creationOptions = options ?? {};
		this.exitStatus = undefined;
		this.shellIntegration = undefined;
		this.state = { isInteractedWith: false, shell: undefined };
		this.processId = call("terminal/processId", { id }).then(value => value ?? undefined);
	}

	sendText(text, shouldExecute = true) {
		this.state.isInteractedWith = true;
		send("terminal/sendText", { id: this._id, text: String(text), execute: shouldExecute !== false });
	}

	show(preserveFocus) {
		send("terminal/show", { id: this._id, preserveFocus: preserveFocus === true });
	}

	hide() {}

	dispose() {
		send("terminal/dispose", { id: this._id });
		terminals.delete(this._id);
	}
}

const outputChannels = new Map();

const window = {
	get activeTextEditor() {
		return editorFor(state.activeTextEditor);
	},

	get visibleTextEditors() {
		return (state.visibleTextEditors ?? []).map(editorFor).filter(Boolean);
	},

	get activeTerminal() {
		return state.activeTerminalId ? terminals.get(state.activeTerminalId) : undefined;
	},

	get terminals() {
		return [...terminals.values()];
	},

	get activeNotebookEditor() {
		return undefined;
	},

	visibleNotebookEditors: [],

	get state() {
		return { focused: state.focused !== false, active: state.focused !== false };
	},

	get activeColorTheme() {
		return { kind: 2 };
	},

	tabGroups: {
		get all() {
			return tabGroupsCache;
		},
		get activeTabGroup() {
			return tabGroupsCache.find(group => group.isActive) ?? tabGroupsCache[0] ?? { viewColumn: 1, isActive: true, tabs: [], activeTab: undefined };
		},
		onDidChangeTabs: forgeEvent("window.tabGroups.onDidChangeTabs"),
		onDidChangeTabGroups: forgeEvent("window.tabGroups.onDidChangeTabGroups"),
		close: (tabs, preserveFocus) => {
			const list = (Array.isArray(tabs) ? tabs : [tabs]).flatMap(entry => entry?.tabs ?? [entry]).filter(Boolean);
			return call("window/closeTabs", { tabs: list.map(tab => tab._id), preserveFocus: preserveFocus === true }).then(result => result !== false);
		},
	},

	onDidChangeActiveTextEditor: forgeEvent("window.onDidChangeActiveTextEditor"),
	onDidChangeVisibleTextEditors: forgeEvent("window.onDidChangeVisibleTextEditors"),
	onDidChangeTextEditorSelection: forgeEvent("window.onDidChangeTextEditorSelection"),
	onDidChangeTextEditorVisibleRanges: forgeEvent("window.onDidChangeTextEditorVisibleRanges"),
	onDidChangeTextEditorOptions: forgeEvent("window.onDidChangeTextEditorOptions"),
	onDidChangeTextEditorViewColumn: forgeEvent("window.onDidChangeTextEditorViewColumn"),
	onDidChangeWindowState: forgeEvent("window.onDidChangeWindowState"),
	onDidChangeActiveColorTheme: forgeEvent("window.onDidChangeActiveColorTheme"),
	onDidOpenTerminal: forgeEvent("window.onDidOpenTerminal"),
	onDidCloseTerminal: forgeEvent("window.onDidCloseTerminal"),
	onDidChangeActiveTerminal: forgeEvent("window.onDidChangeActiveTerminal"),
	onDidChangeTerminalState: forgeEvent("window.onDidChangeTerminalState"),
	onDidChangeTerminalShellIntegration: forgeEvent("window.onDidChangeTerminalShellIntegration"),
	onDidStartTerminalShellExecution: forgeEvent("window.onDidStartTerminalShellExecution"),
	onDidEndTerminalShellExecution: forgeEvent("window.onDidEndTerminalShellExecution"),
	onDidChangeActiveNotebookEditor: noopEvent,
	onDidChangeVisibleNotebookEditors: noopEvent,
	onDidChangeNotebookEditorSelection: noopEvent,

	showErrorMessage: (message, ...items) => showMessage("error", message, items),
	showWarningMessage: (message, ...items) => showMessage("warning", message, items),
	showInformationMessage: (message, ...items) => showMessage("info", message, items),
	showInputBox: options => call("window/showInputBox", { options: toPlain(options ?? {}) }).then(value => value ?? undefined),

	async showTextDocument(target, columnOrOptions, preserveFocus) {
		const document = target instanceof TextDocument ? target : await openDocument(target?.uri ?? target);
		const options = typeof columnOrOptions === "object" ? columnOrOptions : { viewColumn: columnOrOptions, preserveFocus };

		await call("window/showTextDocument", {
			uri: document.uri.toJSON(),
			text: document.uri.scheme === "file" ? undefined : document.getText(),
			options: toPlain(options ?? {}),
		});

		return editorFor({ document: { uri: document.uri.toJSON() }, selection: options?.selection ? { anchor: options.selection.start, active: options.selection.end } : undefined }) ?? new TextEditor(document);
	},

	createOutputChannel(name, options) {
		let channel = outputChannels.get(name);
		if (channel) return channel;

		const line = (level, value, ...rest) => send("output/append", { name, value: `[${level}] ${value}${rest.length ? ` ${rest.map(r => typeof r === "string" ? r : r instanceof Error ? r.stack ?? r.message : JSON.stringify(r)).join(" ")}` : ""}\n` });

		channel = {
			name,
			logLevel: 2,
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

	createStatusBarItem(idOrAlignment, alignmentOrPriority, maybePriority) {
		const named = typeof idOrAlignment === "string";
		const id = named ? idOrAlignment : `status-${Math.random().toString(36).slice(2)}`;
		const alignment = named ? alignmentOrPriority : idOrAlignment;
		const priority = named ? maybePriority : alignmentOrPriority;
		let visible = false;

		const values = { text: "", tooltip: "", command: undefined, color: undefined, backgroundColor: undefined, name: undefined, accessibilityInformation: undefined };
		const publish = () => {
			if (!visible) return;
			const command = typeof values.command === "object" ? values.command?.command : values.command;
			send("status/set", { id, text: values.text, tooltip: typeof values.tooltip === "object" ? values.tooltip?.value : values.tooltip, command, color: values.color?.id ?? values.color, backgroundColor: values.backgroundColor?.id, alignment, priority });
		};

		const item = {
			id,
			alignment,
			priority,
			show: () => {
				visible = true;
				publish();
			},
			hide: () => {
				visible = false;
				send("status/remove", { id });
			},
			dispose: () => {
				visible = false;
				send("status/remove", { id });
			},
		};

		for (const key of Object.keys(values)) {
			Object.defineProperty(item, key, {
				get: () => values[key],
				set: value => {
					values[key] = value;
					publish();
				},
			});
		}

		return item;
	},

	setStatusBarMessage(text, hideAfterOrThenable) {
		const id = `message-${Math.random().toString(36).slice(2)}`;
		send("status/set", { id, text: String(text), alignment: 1, priority: -1000 });
		const dispose = () => send("status/remove", { id });
		if (typeof hideAfterOrThenable === "number") setTimeout(dispose, hideAfterOrThenable);
		else if (hideAfterOrThenable?.then) hideAfterOrThenable.then(dispose, dispose);
		return new Disposable(dispose);
	},

	createTextEditorDecorationType(options) {
		const key = `decoration-${Math.random().toString(36).slice(2)}`;
		send("editor/decorationType", { key, options: toPlain(options ?? {}) });
		return { key, dispose: () => send("editor/disposeDecorationType", { key }) };
	},

	createTerminal(nameOrOptions, shellPath, shellArgs) {
		const options = typeof nameOrOptions === "object" ? nameOrOptions : { name: nameOrOptions, shellPath, shellArgs };
		const id = `terminal-${Math.random().toString(36).slice(2)}`;
		const terminal = new Terminal(id, options.name ?? "Terminal", options);

		terminals.set(id, terminal);
		send("terminal/create", {
			id,
			name: options.name ?? null,
			shellPath: options.shellPath ?? null,
			shellArgs: Array.isArray(options.shellArgs) ? options.shellArgs : options.shellArgs ? [options.shellArgs] : [],
			cwd: options.cwd ? (typeof options.cwd === "string" ? options.cwd : options.cwd.fsPath) : null,
			env: options.env ?? null,
			location: toPlain(options.location ?? null),
			hideFromUser: options.hideFromUser === true,
		});

		emitter("window.onDidOpenTerminal").fire(terminal);
		return terminal;
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

	registerUriHandler(handler) {
		uriHandlers.push(handler);
		return new Disposable(() => uriHandlers.splice(uriHandlers.indexOf(handler), 1));
	},

	registerTreeDataProvider() {
		return new Disposable();
	},

	registerFileDecorationProvider() {
		return new Disposable();
	},

	registerTerminalLinkProvider() {
		return new Disposable();
	},

	registerTerminalProfileProvider() {
		return new Disposable();
	},

	registerCustomEditorProvider() {
		return new Disposable();
	},

	createWebviewPanel(viewType, title, showOptions, options) {
		const panel = createWebview(`panel:${viewType}`, title, true);
		panel.viewType = viewType;
		panel.webview.options = options ?? {};
		send("webview/openPanel", { id: `panel:${viewType}`, viewType, title, showOptions: toPlain(showOptions ?? null) });
		return panel;
	},

	withProgress(options, task) {
		const title = options?.title;

		if (title) send("window/progress", { title, state: "start" });

		const source = new CancellationTokenSource();
		const progress = { report: value => send("window/progress", { title, state: "report", message: value?.message, increment: value?.increment }) };

		return Promise.resolve(task(progress, source.token)).finally(() => {
			if (title) send("window/progress", { title, state: "end" });
			source.dispose();
		});
	},

	showQuickPick: async (items, options) => {
		const resolved = await items;
		const plain = resolved.map(item => typeof item === "string" ? item : { label: item.label, description: item.description, detail: item.detail, picked: item.picked });
		const picked = await call("window/showQuickPick", { items: plain, options: toPlain(options ?? {}) });
		if (picked === null || picked === undefined) return undefined;
		const index = plain.findIndex(item => (typeof item === "string" ? item : item.label) === (typeof picked === "string" ? picked : picked.label));
		return index >= 0 ? resolved[index] : undefined;
	},

	showOpenDialog: options => call("window/showOpenDialog", { options: toPlain(options ?? {}) }).then(list => (list ?? []).map(Uri.revive)),
	showSaveDialog: options => call("window/showSaveDialog", { options: toPlain(options ?? {}) }).then(value => value ? Uri.revive(value) : undefined),
	showWorkspaceFolderPick: () => Promise.resolve(workspace.workspaceFolders?.[0]),
};

async function showMessage(level, message, items) {
	const flat = items.flat();
	const options = flat.length && typeof flat[0] === "object" && !("title" in flat[0]) ? flat.shift() : undefined;
	const plain = flat.map(item => typeof item === "string" ? item : { title: item.title });
	const picked = await call("window/showMessage", { level, message: String(message), modal: options?.modal === true, detail: options?.detail ?? null, items: plain });
	if (picked === null || picked === undefined) return undefined;
	const index = plain.findIndex(item => (typeof item === "string" ? item : item.title) === (typeof picked === "string" ? picked : picked.title));
	return index >= 0 ? flat[index] : undefined;
}

const uriHandlers = [];

class FileSystemError extends Error {
	constructor(message, code) {
		super(message instanceof Uri ? message.toString() : String(message ?? code ?? "Unknown"));
		this.code = code ?? "Unknown";
		this.name = `${this.code} (FileSystemError)`;
	}

	static FileNotFound(message) {
		return new FileSystemError(message ?? "file not found", "FileNotFound");
	}

	static FileExists(message) {
		return new FileSystemError(message ?? "file exists", "FileExists");
	}

	static FileNotADirectory(message) {
		return new FileSystemError(message ?? "not a directory", "FileNotADirectory");
	}

	static FileIsADirectory(message) {
		return new FileSystemError(message ?? "is a directory", "FileIsADirectory");
	}

	static NoPermissions(message) {
		return new FileSystemError(message ?? "no permissions", "NoPermissions");
	}

	static Unavailable(message) {
		return new FileSystemError(message ?? "unavailable", "Unavailable");
	}
}

const FileType = { Unknown: 0, File: 1, Directory: 2, SymbolicLink: 64 };

const nodeFs = require("fs");
const nodePath = require("path");

function wrapFsError(error, uri) {
	if (error instanceof FileSystemError) return error;
	switch (error?.code) {
		case "ENOENT": return FileSystemError.FileNotFound(uri);
		case "EEXIST": return FileSystemError.FileExists(uri);
		case "ENOTDIR": return FileSystemError.FileNotADirectory(uri);
		case "EISDIR": return FileSystemError.FileIsADirectory(uri);
		case "EACCES":
		case "EPERM": return FileSystemError.NoPermissions(uri);
		default: return error;
	}
}

async function guard(uri, action) {
	try {
		return await action();
	} catch (error) {
		throw wrapFsError(error, uri);
	}
}

const fs = {
	async stat(target) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.stat(uri);

		return guard(uri, async () => {
			const info = await nodeFs.promises.stat(uri.fsPath);
			return { type: info.isDirectory() ? FileType.Directory : FileType.File, ctime: info.ctimeMs, mtime: info.mtimeMs, size: info.size };
		});
	},

	async readFile(target) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.readFile(uri);
		if (contentProviders.has(uri.scheme)) return Buffer.from(await readVirtual(uri), "utf8");
		return guard(uri, () => nodeFs.promises.readFile(uri.fsPath));
	},

	async writeFile(target, content) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.writeFile(uri, content, { create: true, overwrite: true });

		return guard(uri, async () => {
			await nodeFs.promises.mkdir(nodePath.dirname(uri.fsPath), { recursive: true });
			await nodeFs.promises.writeFile(uri.fsPath, content);
		});
	},

	async readDirectory(target) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.readDirectory(uri);

		return guard(uri, async () => (await nodeFs.promises.readdir(uri.fsPath, { withFileTypes: true })).map(entry => [entry.name, entry.isDirectory() ? FileType.Directory : entry.isSymbolicLink() ? FileType.SymbolicLink : FileType.File]));
	},

	async createDirectory(target) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.createDirectory(uri);
		return guard(uri, () => nodeFs.promises.mkdir(uri.fsPath, { recursive: true }));
	},

	async delete(target, options) {
		const uri = Uri.revive(target);
		const provider = fileSystemProviders.get(uri.scheme);
		if (provider) return provider.delete(uri, { recursive: options?.recursive === true });
		return guard(uri, () => nodeFs.promises.rm(uri.fsPath, { recursive: options?.recursive === true, force: false }));
	},

	async rename(source, target, options) {
		const from = Uri.revive(source);
		const to = Uri.revive(target);
		const provider = fileSystemProviders.get(from.scheme);
		if (provider) return provider.rename(from, to, { overwrite: options?.overwrite === true });

		return guard(from, async () => {
			if (options?.overwrite !== true && nodeFs.existsSync(to.fsPath)) throw FileSystemError.FileExists(to);
			await nodeFs.promises.rename(from.fsPath, to.fsPath);
		});
	},

	async copy(source, target, options) {
		const from = Uri.revive(source);
		const to = Uri.revive(target);
		return guard(from, () => nodeFs.promises.cp(from.fsPath, to.fsPath, { recursive: true, force: options?.overwrite === true, errorOnExist: options?.overwrite !== true }));
	},

	isWritableFileSystem(scheme) {
		if (scheme === "file") return true;
		const provider = fileSystemProviders.get(scheme);
		return provider ? !provider.isReadonly : undefined;
	},
};

class RelativePattern {
	constructor(base, pattern) {
		this.baseUri = typeof base === "string" ? Uri.file(base) : base?.uri ?? Uri.revive(base);
		this.base = this.baseUri.fsPath;
		this.pattern = pattern;
	}
}

const defaultExcludes = ["**/node_modules/**", "**/.git/**", "**/bin/**", "**/obj/**", "**/artifacts/**"];

async function findFiles(include, exclude, maxResults, token) {
	const root = include instanceof RelativePattern ? include.base : workspace.workspaceFolders?.[0]?.uri?.fsPath;
	if (!root) return [];

	const pattern = include instanceof RelativePattern ? include.pattern : String(include ?? "**/*");
	const excludes = exclude === null ? [] : exclude ? [exclude instanceof RelativePattern ? exclude.pattern : String(exclude)] : defaultExcludes;
	const excluders = excludes.flatMap(entry => expandBraces(entry)).map(entry => new Bun.Glob(entry));
	const includers = expandBraces(pattern).map(entry => new Bun.Glob(entry));
	const results = [];
	const seen = new Set();

	for (const glob of includers) {
		for await (const relative of glob.scan({ cwd: root, onlyFiles: true, dot: true })) {
			if (token?.isCancellationRequested) return results;
			const normalised = relative.replace(/\\/g, "/");
			if (seen.has(normalised) || excluders.some(excluder => excluder.match(normalised))) continue;
			seen.add(normalised);
			results.push(Uri.file(nodePath.join(root, relative)));
			if (maxResults && results.length >= maxResults) return results;
		}
	}

	return results;
}

function expandBraces(pattern) {
	const match = /\{([^{}]*)\}/.exec(pattern);
	if (!match) return [pattern];
	return match[1].split(",").flatMap(part => expandBraces(pattern.slice(0, match.index) + part + pattern.slice(match.index + match[0].length)));
}

function workspaceFolderFor(target) {
	const uri = Uri.revive(target);
	if (!uri || uri.scheme !== "file") return undefined;
	const path = uri.fsPath.toLowerCase();
	return (workspace.workspaceFolders ?? []).find(folder => {
		const root = folder.uri.fsPath.toLowerCase().replace(/[\\/]+$/, "");
		return path === root || path.startsWith(`${root}\\`) || path.startsWith(`${root}/`);
	});
}

const configurationDefaults = {};

function registerConfigurationDefaults(manifest) {
	const contributed = manifest?.contributes?.configuration;
	const sections = Array.isArray(contributed) ? contributed : contributed ? [contributed] : [];

	for (const section of sections) {
		for (const [key, schema] of Object.entries(section?.properties ?? {})) {
			if (schema && "default" in schema && !(key in configurationDefaults)) configurationDefaults[key] = schema.default;
		}
	}
}

function lookupConfiguration(source, fullKey) {
	if (fullKey in source) return source[fullKey];

	const prefix = `${fullKey}.`;
	let result;

	for (const [key, value] of Object.entries(source)) {
		if (!key.startsWith(prefix)) continue;
		result ??= {};
		let cursor = result;
		const parts = key.slice(prefix.length).split(".");
		for (let i = 0; i < parts.length - 1; i++) cursor = cursor[parts[i]] ??= {};
		cursor[parts.at(-1)] = value;
	}

	return result;
}

function isPlainObject(value) {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

function mergeConfiguration(base, override) {
	if (override === undefined) return base;
	if (!isPlainObject(base) || !isPlainObject(override)) return override;

	const merged = { ...base };
	for (const [key, value] of Object.entries(override)) merged[key] = mergeConfiguration(base[key], value);
	return merged;
}

function configurationValue(fullKey) {
	return mergeConfiguration(lookupConfiguration(configurationDefaults, fullKey), lookupConfiguration(state.configuration, fullKey));
}

function configurationChange(previous, next) {
	const changed = new Set();

	for (const key of new Set([...Object.keys(previous ?? {}), ...Object.keys(next ?? {})])) {
		if (JSON.stringify(previous?.[key]) !== JSON.stringify(next?.[key])) changed.add(key);
	}

	if (changed.size === 0) return null;

	const keys = [...changed];

	return {
		affectsConfiguration: section => keys.some(key => key === section || key.startsWith(`${section}.`) || section.startsWith(`${key}.`)),
	};
}

const workspace = {
	get workspaceFolders() {
		return state.workspaceFolders;
	},

	get rootPath() {
		return state.workspaceFolders?.[0]?.uri?.fsPath;
	},

	get workspaceFile() {
		return undefined;
	},

	get textDocuments() {
		return [...documents.values()].filter(document => !document.isClosed);
	},

	get name() {
		return state.workspaceFolders?.[0]?.name;
	},

	get isTrusted() {
		return true;
	},

	fs,

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
	onWillCreateFiles: noopEvent,
	onWillDeleteFiles: noopEvent,
	onWillRenameFiles: noopEvent,
	onDidGrantWorkspaceTrust: noopEvent,
	onDidOpenNotebookDocument: noopEvent,
	onDidCloseNotebookDocument: noopEvent,
	onDidChangeNotebookDocument: noopEvent,
	onDidSaveNotebookDocument: noopEvent,
	notebookDocuments: [],

	getConfiguration(section) {
		const full = key => (section ? (key ? `${section}.${key}` : section) : key);

		return {
			get: (key, fallback) => configurationValue(full(key)) ?? fallback,
			has: key => configurationValue(full(key)) !== undefined,
			update: (key, value, target) => call("workspace/updateConfiguration", { key: full(key), value: toPlain(value), target: target ?? null }),
			inspect: key => {
				const name = full(key);
				return { key: name, defaultValue: lookupConfiguration(configurationDefaults, name), globalValue: lookupConfiguration(state.configuration, name), workspaceValue: undefined, workspaceFolderValue: undefined };
			},
		};
	},

	getWorkspaceFolder: workspaceFolderFor,

	asRelativePath(target, includeWorkspaceFolder) {
		const path = typeof target === "string" ? target : Uri.revive(target)?.fsPath ?? "";
		const folder = workspaceFolderFor(typeof target === "string" ? Uri.file(target) : target);
		if (!folder) return path;
		const relative = path.slice(folder.uri.fsPath.replace(/[\\/]+$/, "").length).replace(/^[\\/]/, "").replace(/\\/g, "/");
		return includeWorkspaceFolder && (workspace.workspaceFolders?.length ?? 0) > 1 ? `${folder.name}/${relative}` : relative;
	},

	async openTextDocument(target) {
		if (target === undefined || (typeof target === "object" && !(target instanceof Uri) && !target.scheme && !target.path)) {
			const options = target ?? {};
			const uri = new Uri("untitled", "", `/Untitled-${documents.size + 1}`, "", "");
			const document = trackDocument(new TextDocument(uri, options.content ?? "", options.language ?? "plaintext"));
			emitter("workspace.onDidOpenTextDocument").fire(document);
			return document;
		}

		return openDocument(typeof target === "string" ? Uri.file(target) : target);
	},

	async applyEdit(edit) {
		const files = edit._files ?? [];
		const plain = [];

		for (const operation of files) {
			if (operation.kind === "create") await fs.writeFile(operation.uri, new Uint8Array());
			else if (operation.kind === "delete") await fs.delete(operation.uri, { recursive: operation.options?.recursive === true });
			else if (operation.kind === "rename") await fs.rename(operation.uri, operation.target, { overwrite: operation.options?.overwrite === true });
		}

		for (const [uri, edits] of edit.entries()) {
			const document = findDocument(uri);

			if (uri.scheme !== "file" && document) {
				const sorted = edits.map(entry => ({ start: document.offsetAt(entry.range.start), end: document.offsetAt(entry.range.end), text: entry.newText })).sort((a, b) => b.start - a.start);
				const changes = sorted.map(entry => ({ offset: entry.start, length: entry.end - entry.start, text: entry.text }));
				const event = document._applyChanges(changes);
				document.isDirty = true;
				send("documents/virtualChanged", { uri: uri.toJSON(), text: document.getText() });
				emitter("workspace.onDidChangeTextDocument").fire(event);
				continue;
			}

			plain.push({ uri: uri.toJSON(), edits: edits.map(entry => ({ range: toPlain(entry.range), newText: entry.newText })) });
		}

		if (plain.length === 0) return true;
		return (await call("workspace/applyEdit", { entries: plain })) === true;
	},

	findFiles,

	async save(target) {
		const document = findDocument(target);
		if (!document) return undefined;
		return (await document.save()) ? document.uri : undefined;
	},

	async saveAll() {
		await Promise.all(workspace.textDocuments.filter(document => document.isDirty).map(document => document.save()));
		return true;
	},

	registerFileSystemProvider(scheme, provider) {
		fileSystemProviders.set(scheme, provider);
		const subscription = provider.onDidChangeFile?.(changes => {
			for (const change of changes ?? []) {
				const document = findDocument(change.uri);
				if (!document || change.type !== 1) continue;
				Promise.resolve(provider.readFile(change.uri)).then(bytes => {
					const text = Buffer.from(bytes).toString("utf8");
					if (text === document.getText()) return;
					const event = document._applyChanges([{ offset: 0, length: document.getText().length, text }]);
					send("documents/virtualChanged", { uri: document.uri.toJSON(), text });
					emitter("workspace.onDidChangeTextDocument").fire(event);
				}, () => {});
			}
		});

		return new Disposable(() => {
			fileSystemProviders.delete(scheme);
			subscription?.dispose?.();
		});
	},

	registerTextDocumentContentProvider(scheme, provider) {
		contentProviders.set(scheme, provider);
		const subscription = provider.onDidChange?.(uri => {
			const document = findDocument(uri);
			if (!document) return;
			readVirtual(uri).then(text => {
				if (text === document.getText()) return;
				const event = document._applyChanges([{ offset: 0, length: document.getText().length, text }]);
				send("documents/virtualChanged", { uri: document.uri.toJSON(), text });
				emitter("workspace.onDidChangeTextDocument").fire(event);
			}, () => {});
		});

		return new Disposable(() => {
			contentProviders.delete(scheme);
			subscription?.dispose?.();
		});
	},

	createFileSystemWatcher: () => ({
		ignoreCreateEvents: false,
		ignoreChangeEvents: false,
		ignoreDeleteEvents: false,
		onDidCreate: noopEvent,
		onDidChange: noopEvent,
		onDidDelete: noopEvent,
		dispose: () => {},
	}),

	updateWorkspaceFolders: () => false,
	registerTaskProvider: () => new Disposable(),
};

class Diagnostic {
	constructor(diagnosticRange, message, severity) {
		this.range = diagnosticRange;
		this.message = message;
		this.severity = severity ?? 0;
	}
}

const diagnosticsByUri = new Map();

function convertDiagnostic(entry) {
	const diagnostic = new Diagnostic(range(entry.range), entry.message, entry.severity);
	diagnostic.source = entry.source;
	diagnostic.code = entry.code ?? undefined;
	return diagnostic;
}

documentHandlers["languages/setDiagnostics"] = params => {
	const changed = new Set(diagnosticsByUri.keys());
	diagnosticsByUri.clear();

	for (const [target, list] of params?.entries ?? []) {
		const uri = Uri.revive(target);
		const key = uri.toString();
		diagnosticsByUri.set(key, { uri, list: (list ?? []).map(convertDiagnostic) });
		changed.add(key);
	}

	emitter("languages.onDidChangeDiagnostics").fire({ uris: [...changed].map(key => diagnosticsByUri.get(key)?.uri ?? Uri.parse(key)) });
	return null;
};

const languages = {
	getDiagnostics(uri) {
		if (uri) return diagnosticsByUri.get(uriKey(uri))?.list ?? [];
		return [...diagnosticsByUri.values()].map(entry => [entry.uri, entry.list]);
	},
	onDidChangeDiagnostics: forgeEvent("languages.onDidChangeDiagnostics"),
	createDiagnosticCollection: name => {
		const entries = new Map();
		return {
			name,
			set: (uri, list) => entries.set(uriKey(uri), list),
			delete: uri => entries.delete(uriKey(uri)),
			clear: () => entries.clear(),
			forEach: callback => entries.forEach((list, key) => callback(Uri.parse(key), list)),
			get: uri => entries.get(uriKey(uri)),
			has: uri => entries.has(uriKey(uri)),
			dispose: () => entries.clear(),
		};
	},
	getLanguages: () => Promise.resolve([...new Set(Object.values(languageIds))]),
	setTextDocumentLanguage: (document, languageId) => {
		document.languageId = languageId;
		return Promise.resolve(document);
	},
	match: () => 0,
	registerCodeActionsProvider: () => new Disposable(),
	registerCodeLensProvider: () => new Disposable(),
	registerHoverProvider: () => new Disposable(),
	registerCompletionItemProvider: () => new Disposable(),
	registerDefinitionProvider: () => new Disposable(),
	registerDocumentLinkProvider: () => new Disposable(),
	registerInlineCompletionItemProvider: () => new Disposable(),
};

const telemetryChanged = new EventEmitter();

const env = {
	appName: "Visual Studio Code",
	appRoot: __dirname,
	appHost: "desktop",
	uriScheme: process.env.FORGE_URI_SCHEME || "vscode",
	language: "en",
	machineId: "forge",
	sessionId: `forge-${Date.now()}`,
	remoteName: undefined,
	uiKind: 1,
	isNewAppInstall: false,
	isTelemetryEnabled: false,
	onDidChangeTelemetryEnabled: telemetryChanged.event,
	onDidChangeShell: noopEvent,
	logLevel: 2,
	onDidChangeLogLevel: noopEvent,
	get shell() {
		return state.shell ?? process.env.ComSpec ?? "cmd.exe";
	},
	clipboard: {
		readText: () => call("env/clipboardRead", {}).then(value => value ?? ""),
		writeText: value => call("env/clipboardWrite", { value: String(value) }),
	},
	openExternal: target => call("env/openExternal", { target: String(target) }).then(value => value !== false),
	asExternalUri: target => Promise.resolve(Uri.revive(target)),
	createTelemetryLogger: () => ({ logUsage() {}, logError() {}, dispose() {}, isUsageEnabled: false, isErrorsEnabled: false, onDidChangeEnableStates: noopEvent }),
};

const extensionRegistry = new Map();

const extensions = {
	getExtension: id => extensionRegistry.get(String(id).toLowerCase()),
	get all() {
		return [...extensionRegistry.values()];
	},
	onDidChange: noopEvent,
};

function registerExtension(id, directory, manifest) {
	registerConfigurationDefaults(manifest);

	const entry = {
		id,
		extensionPath: directory,
		extensionUri: Uri.file(directory),
		packageJSON: manifest,
		extensionKind: 1,
		isActive: false,
		exports: undefined,
		activate: () => Promise.resolve(entry.exports),
	};

	extensionRegistry.set(id.toLowerCase(), entry);
	return entry;
}

class CancellationTokenSource {
	constructor() {
		const changed = new EventEmitter();
		this.token = { isCancellationRequested: false, onCancellationRequested: changed.event };
		this._emitter = changed;
	}

	cancel() {
		if (this.token.isCancellationRequested) return;
		this.token.isCancellationRequested = true;
		this._emitter.fire();
	}

	dispose() {
		this._emitter.dispose();
	}
}

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
	const disposed = new EventEmitter();
	const visibility = new EventEmitter();

	const webview = {
		options: {},
		cspSource: "https://forge-webview",
		onDidReceiveMessage: messages.event,
		postMessage: message => call("webview/postMessage", { id, message }).then(() => true),
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
		description: undefined,
		badge: undefined,
		webview,
		visible: true,
		onDidDispose: disposed.event,
		onDidChangeVisibility: visibility.event,
		show: () => send("webview/show", { id }),
		reveal: () => send("webview/show", { id }),
		dispose: () => {
			if (!webviews.has(id)) return;
			webviews.delete(id);
			send("webview/dispose", { id });
			disposed.fire();
		},
	};

	if (panel) {
		const viewState = new EventEmitter();
		view.viewColumn = 1;
		view.active = true;
		view.options = {};
		view.iconPath = undefined;
		view.onDidChangeViewState = viewState.event;
		view._viewState = viewState;
		view.reveal = column => {
			if (typeof column === "number") view.viewColumn = column;
			send("webview/show", { id });
		};
	}

	webviews.set(id, { view, messages, disposed, visibility });
	return view;
}

const state = {
	activeTextEditor: undefined,
	visibleTextEditors: [],
	activeTerminalId: null,
	tabGroups: [],
	workspaceFolders: undefined,
	configuration: {},
	focused: true,
	shell: undefined,
};

function applyState(next) {
	if (!next) return;

	const previousActive = state.activeTextEditor?.document?.uri ? uriKey(state.activeTextEditor.document.uri) : null;
	const previousVisible = (state.visibleTextEditors ?? []).map(editor => uriKey(editor.document.uri)).join("|");
	const previousSelection = JSON.stringify(state.activeTextEditor?.selections ?? null);
	const previousTabs = JSON.stringify(state.tabGroups ?? []);
	const previousFocused = state.focused;
	const previousConfiguration = state.configuration;

	if ("workspaceFolders" in next) {
		state.workspaceFolders = next.workspaceFolders ? next.workspaceFolders.map(folder => ({ name: folder.name, index: folder.index, uri: Uri.revive(folder.uri) })) : undefined;
	}

	for (const key of ["activeTextEditor", "visibleTextEditors", "configuration", "focused", "shell"]) if (key in next) state[key] = next[key];

	if ("configuration" in next) {
		const change = configurationChange(previousConfiguration, state.configuration);
		if (change) emitter("workspace.onDidChangeConfiguration").fire(change);
	}

	if ("tabGroups" in next) {
		state.tabGroups = next.tabGroups;
		buildTabGroups(next.tabGroups);
		if (JSON.stringify(next.tabGroups ?? []) !== previousTabs) {
			emitter("window.tabGroups.onDidChangeTabs").fire({ opened: [], closed: [], changed: window.tabGroups.all.flatMap(group => group.tabs) });
			emitter("window.tabGroups.onDidChangeTabGroups").fire({ opened: [], closed: [], changed: window.tabGroups.all });
		}
	}

	const active = state.activeTextEditor?.document?.uri ? uriKey(state.activeTextEditor.document.uri) : null;
	const visible = (state.visibleTextEditors ?? []).map(editor => uriKey(editor.document.uri)).join("|");

	if (active !== previousActive) emitter("window.onDidChangeActiveTextEditor").fire(window.activeTextEditor);
	if (visible !== previousVisible) emitter("window.onDidChangeVisibleTextEditors").fire(window.visibleTextEditors);

	if (active && JSON.stringify(state.activeTextEditor?.selections ?? null) !== previousSelection) {
		const editor = window.activeTextEditor;
		if (editor) emitter("window.onDidChangeTextEditorSelection").fire({ textEditor: editor, selections: editor.selections, kind: 1 });
	}

	if (previousFocused !== state.focused) emitter("window.onDidChangeWindowState").fire(window.state);
}

class ThemeColor {
	constructor(id) {
		this.id = id;
	}
}

class ThemeIcon {
	constructor(id, color) {
		this.id = id;
		this.color = color;
	}
}

ThemeIcon.File = new ThemeIcon("file");
ThemeIcon.Folder = new ThemeIcon("folder");

class MarkdownString {
	constructor(value, supportThemeIcons) {
		this.value = value ?? "";
		this.isTrusted = false;
		this.supportThemeIcons = supportThemeIcons === true;
		this.supportHtml = false;
	}

	appendMarkdown(value) {
		this.value += value;
		return this;
	}

	appendText(value) {
		this.value += String(value).replace(/[\\`*_{}[\]()#+\-.!|]/g, "\\$&");
		return this;
	}

	appendCodeblock(value, language) {
		this.value += `\n\`\`\`${language ?? ""}\n${value}\n\`\`\`\n`;
		return this;
	}
}

class SnippetString {
	constructor(value) {
		this.value = value ?? "";
	}

	appendText(value) {
		this.value += String(value).replace(/[$}\\]/g, "\\$&");
		return this;
	}

	appendPlaceholder(value, number) {
		this.value += `\${${number ?? 1}:${value}}`;
		return this;
	}

	appendTabstop(number) {
		this.value += `$${number ?? 0}`;
		return this;
	}
}

class TreeItem {
	constructor(labelOrUri, collapsibleState) {
		if (labelOrUri instanceof Uri) this.resourceUri = labelOrUri;
		else this.label = labelOrUri;
		this.collapsibleState = collapsibleState ?? 0;
	}
}

class FileDecoration {
	constructor(badge, tooltip, color) {
		this.badge = badge;
		this.tooltip = tooltip;
		this.color = color;
	}
}

const api = {
	version: "1.130.0",
	Disposable,
	EventEmitter,
	Uri,
	Position,
	Range,
	Selection,
	Location,
	TextEdit,
	WorkspaceEdit,
	TextDocument,
	TextLine,
	FileSystemError,
	RelativePattern,
	Diagnostic,
	ThemeColor,
	ThemeIcon,
	MarkdownString,
	SnippetString,
	TreeItem,
	FileDecoration,
	CancellationTokenSource,
	CancellationError: class CancellationError extends Error {
		constructor() {
			super("Canceled");
			this.name = "Canceled";
		}
	},
	commands,
	window,
	workspace,
	languages,
	env,
	extensions,
	l10n: { t: (message, ...args) => String(typeof message === "object" ? message.message : message).replace(/\{(\d+)\}/g, (_, index) => String(args[Number(index)] ?? "")), bundle: undefined, uri: undefined },
	ViewColumn: { Active: -1, Beside: -2, One: 1, Two: 2, Three: 3, Four: 4, Five: 5, Six: 6, Seven: 7, Eight: 8, Nine: 9 },
	ProgressLocation: { SourceControl: 1, Window: 10, Notification: 15 },
	QuickPickItemKind: { Separator: -1, Default: 0 },
	UIKind: { Desktop: 1, Web: 2 },
	TextEditorRevealType: { Default: 0, InCenter: 1, InCenterIfOutsideViewport: 2, AtTop: 3 },
	TextEditorSelectionChangeKind: { Keyboard: 1, Mouse: 2, Command: 3 },
	TextEditorCursorStyle: { Line: 1, Block: 2, Underline: 3, LineThin: 4, BlockOutline: 5, UnderlineThin: 6 },
	TextEditorLineNumbersStyle: { Off: 0, On: 1, Relative: 2 },
	DecorationRangeBehavior: { OpenOpen: 0, ClosedClosed: 1, OpenClosed: 2, ClosedOpen: 3 },
	OverviewRulerLane: { Left: 1, Center: 2, Right: 4, Full: 7 },
	StatusBarAlignment: { Left: 1, Right: 2 },
	DiagnosticSeverity: { Error: 0, Warning: 1, Information: 2, Hint: 3 },
	DiagnosticTag: { Unnecessary: 1, Deprecated: 2 },
	FileType,
	FileChangeType: { Changed: 1, Created: 2, Deleted: 3 },
	ConfigurationTarget: { Global: 1, Workspace: 2, WorkspaceFolder: 3 },
	ExtensionMode: { Production: 1, Development: 2, Test: 3 },
	ExtensionKind: { UI: 1, Workspace: 2 },
	EndOfLine,
	TextDocumentChangeReason,
	TextDocumentSaveReason,
	TreeItemCollapsibleState: { None: 0, Collapsed: 1, Expanded: 2 },
	ColorThemeKind: { Light: 1, Dark: 2, HighContrast: 3, HighContrastLight: 4 },
	LogLevel: { Off: 0, Trace: 1, Debug: 2, Info: 3, Warning: 4, Error: 5 },
	TerminalLocation: { Panel: 1, Editor: 2 },
	TerminalExitReason: { Unknown: 0, Shutdown: 1, Process: 2, User: 3, Extension: 4 },
	TerminalShellExecutionCommandLineConfidence: { Low: 0, Medium: 1, High: 2 },
	EnvironmentVariableMutatorType: { Replace: 1, Append: 2, Prepend: 3 },
	NotebookCellKind: { Markup: 1, Code: 2 },
	InputBoxValidationSeverity: { Info: 1, Warning: 2, Error: 3 },
	QuickInputButtons: { Back: { iconPath: new ThemeIcon("arrow-left") } },
	TabInputText,
	TabInputTextDiff,
	TabInputCustom,
	TabInputWebview,
	TabInputNotebook,
	TabInputNotebookDiff,
	TabInputTerminal,
	NotebookCellOutputItem: {
		text: (value, mime) => ({ mime: mime ?? "text/plain", data: Buffer.from(String(value)) }),
		error: value => ({ mime: "application/vnd.code.notebook.error", data: Buffer.from(JSON.stringify({ name: value?.name, message: value?.message, stack: value?.stack })) }),
		stdout: value => ({ mime: "application/vnd.code.notebook.stdout", data: Buffer.from(String(value)) }),
		stderr: value => ({ mime: "application/vnd.code.notebook.stderr", data: Buffer.from(String(value)) }),
		json: value => ({ mime: "text/x-json", data: Buffer.from(JSON.stringify(value)) }),
	},
	NotebookCellOutput: class NotebookCellOutput {
		constructor(items, metadata) {
			this.items = items;
			this.metadata = metadata;
		}
	},
	NotebookRange: class NotebookRange {
		constructor(start, end) {
			this.start = start;
			this.end = end;
		}
	},
};

module.exports = api;
module.exports.__forge = { connect, state, applyState, dispatchEvent, commandHandlers, webviewProviders, webviews, createWebview, documentHandlers, registerExtension, uriHandlers, Uri };
