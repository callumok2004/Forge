const Module = require("module");
const path = require("path");
const fs = require("fs");

const { Rpc } = require("./rpc.js");
const vscode = require("./vscode.js");

const forge = vscode.__forge;

const originalResolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) {
	if (request === "vscode") return path.join(__dirname, "vscode.js");
	return originalResolve.call(this, request, ...rest);
};

const rpc = new Rpc(process.stdin, process.stdout);
forge.connect(rpc);

const log = (level, message) => rpc.notify("host/log", { level, message: String(message) });

process.on("uncaughtException", error => log("error", error?.stack ?? error));
process.on("unhandledRejection", error => log("error", error?.stack ?? error));

const loaded = new Map();

function subscriptionsFor(id) {
	const entry = loaded.get(id);
	return entry ? entry.subscriptions : [];
}

rpc.on("host/initialize", params => {
	Object.assign(forge.state, params?.state ?? {});
	return { version: process.versions.bun ? `bun ${process.versions.bun}` : process.version };
});

rpc.on("host/state", params => {
	Object.assign(forge.state, params ?? {});
	return null;
});

rpc.on("host/event", params => {
	forge.dispatchEvent(params.name, params.value);
	return null;
});

rpc.on("extension/activate", async params => {
	const directory = params.directory;
	const manifest = JSON.parse(fs.readFileSync(path.join(directory, "package.json"), "utf8"));
	const id = `${manifest.publisher}.${manifest.name}`;

	if (loaded.has(id)) return { id, alreadyActive: true };

	const entryPoint = path.resolve(directory, manifest.main ?? "extension.js");
	const subscriptions = [];

	const storage = path.join(params.storageDirectory ?? directory, id);
	fs.mkdirSync(storage, { recursive: true });

	const context = {
		subscriptions,
		extensionPath: directory,
		extensionUri: vscode.Uri.file(directory),
		extensionMode: vscode.ExtensionMode.Production,
		globalStorageUri: vscode.Uri.file(storage),
		globalStoragePath: storage,
		storageUri: vscode.Uri.file(storage),
		storagePath: storage,
		logUri: vscode.Uri.file(storage),
		logPath: storage,
		asAbsolutePath: relative => path.join(directory, relative),
		environmentVariableCollection: environmentVariables(),
		globalState: memento(path.join(storage, "global-state.json")),
		workspaceState: memento(path.join(storage, "workspace-state.json")),
		secrets: secrets(path.join(storage, "secrets.json")),
		extension: { id, packageJSON: manifest },
	};

	const module = require(entryPoint);

	loaded.set(id, { module, subscriptions, manifest, directory });

	if (typeof module.activate === "function") await module.activate(context);

	return { id, commands: [...forge.commandHandlers.keys()], webviewProviders: [...forge.webviewProviders.keys()] };
});

rpc.on("extension/deactivate", async params => {
	const entry = loaded.get(params.id);
	if (!entry) return { id: params.id, wasActive: false };

	loaded.delete(params.id);

	for (const item of entry.subscriptions.reverse()) {
		try {
			item?.dispose?.();
		} catch (error) {
			log("error", error?.stack ?? error);
		}
	}

	if (typeof entry.module.deactivate === "function") await entry.module.deactivate();

	return { id: params.id, wasActive: true };
});

rpc.on("commands/invoke", async params => {
	const handler = forge.commandHandlers.get(params.id);
	if (!handler) throw new Error(`unknown command ${params.id}`);
	return (await handler(...(params.args ?? []))) ?? null;
});

rpc.on("webview/resolve", async params => {
	const provider = forge.webviewProviders.get(params.viewId);
	if (!provider) throw new Error(`unknown webview ${params.viewId}`);

	const view = forge.createWebview(params.viewId, params.title ?? params.viewId, false, params.root);
	await provider.resolveWebviewView(view, { state: undefined }, { isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} }) });

	return { viewId: params.viewId, html: view.webview.html };
});

rpc.on("webview/visibility", params => {
	const entry = forge.webviews.get(params.id);
	if (!entry) return null;

	entry.view.visible = params.visible === true;
	forge.dispatchEvent(`webview.${params.id}.onDidChangeVisibility`, undefined);
	return null;
});

rpc.on("webview/message", params => {
	forge.webviews.get(params.id)?.messages.fire(params.message);
	return null;
});

rpc.on("host/shutdown", async () => {
	for (const id of [...loaded.keys()]) {
		try {
			await rpc.handlers.get("extension/deactivate")({ id });
		} catch {
		}
	}
	setTimeout(() => process.exit(0), 10);
	return null;
});

function environmentVariables() {
	const values = new Map();

	const mutate = (kind, variable, value) => {
		values.set(variable, { type: kind, value });
		return undefined;
	};

	const collection = {
		persistent: true,
		description: "",
		get: variable => values.get(variable),
		replace: (variable, value) => mutate(1, variable, value),
		append: (variable, value) => mutate(2, variable, value),
		prepend: (variable, value) => mutate(3, variable, value),
		has: variable => values.has(variable),
		delete: variable => values.delete(variable),
		clear: () => values.clear(),
		forEach: (callback, thisArg) => {
			for (const [variable, mutator] of values) callback.call(thisArg, variable, mutator, collection);
		},
		getScoped: () => collection,
		[Symbol.iterator]: () => values.entries(),
	};

	return collection;
}

function memento(file) {
	let values = {};
	try {
		values = JSON.parse(fs.readFileSync(file, "utf8"));
	} catch {
	}

	const save = () => {
		try {
			fs.writeFileSync(file, JSON.stringify(values));
		} catch (error) {
			log("error", error?.stack ?? error);
		}
	};

	return {
		keys: () => Object.keys(values),
		get: (key, fallback) => values[key] ?? fallback,
		update: (key, value) => {
			if (value === undefined) delete values[key];
			else values[key] = value;
			save();
			return Promise.resolve();
		},
		setKeysForSync: () => {},
	};
}

function secrets(file) {
	const store = memento(file);
	const changed = new vscode.EventEmitter();

	return {
		get: key => Promise.resolve(store.get(key)),
		store: (key, value) => {
			store.update(key, value);
			changed.fire({ key });
			return Promise.resolve();
		},
		delete: key => {
			store.update(key, undefined);
			changed.fire({ key });
			return Promise.resolve();
		},
		onDidChange: changed.event,
	};
}

log("info", "extension host ready");
rpc.notify("host/ready", { runtime: process.versions.bun ? `bun ${process.versions.bun}` : process.version });
