const nodePath = require("path");
const os = require("os");
const { fileURLToPath } = require("url");

const isWindows = process.platform === "win32";
const runtimeDirectory = __dirname;
const shimFile = normalise(nodePath.join(runtimeDirectory, "vscode.js"));
const pipeName = process.env.FORGE_GUARD_PIPE ?? null;

const originals = {
	openSync: require("fs").openSync,
	writeSync: require("fs").writeSync,
	readSync: require("fs").readSync,
	closeSync: require("fs").closeSync,
};

const state = {
	extensions: [],
	workspaceFolders: [],
	temp: normalise(os.tmpdir()),
	ask: null,
	inflight: new Map(),
	depth: 0,
};

function passThrough(self, original, args, constructing) {
	state.depth++;

	try {
		return constructing ? Reflect.construct(original, args, constructing) : original.apply(self, args);
	} finally {
		state.depth--;
	}
}

function normalise(value) {
	const resolved = nodePath.resolve(value);
	return isWindows ? resolved.toLowerCase() : resolved;
}

function inside(child, parent) {
	if (!parent) return false;
	if (child === parent) return true;
	const separator = parent.endsWith(nodePath.sep) ? "" : nodePath.sep;
	return child.startsWith(parent + separator);
}

function toPath(value) {
	if (value === undefined || value === null || typeof value === "number") return null;
	if (value instanceof URL) return value.protocol === "file:" ? fileURLToPath(value) : null;
	if (typeof value === "string" && value.startsWith("file:")) {
		try {
			return fileURLToPath(value);
		} catch {
			return value;
		}
	}
	if (Buffer.isBuffer(value)) return value.toString("utf8");
	return typeof value === "string" ? value : null;
}

function configure(config) {
	state.extensions = (config.extensions ?? []).map(extension => ({
		id: extension.id,
		name: extension.name ?? extension.id,
		directory: normalise(extension.directory),
		storage: (extension.storage ?? []).map(normalise),
		grants: extension.grants ?? {},
		denied: extension.denied ?? {},
	}));
	state.workspaceFolders = (config.workspaceFolders ?? []).map(normalise);
}

function callerExtension() {
	const limit = Error.stackTraceLimit;
	Error.stackTraceLimit = 64;
	const stack = new Error().stack ?? "";
	Error.stackTraceLimit = limit;

	const runtime = normalise(runtimeDirectory);
	let api = false;

	for (const line of stack.split("\n")) {
		const match = /((?:[A-Za-z]:\\|\/)[^()]*?):\d+:\d+\)?\s*$/.exec(line);
		if (!match) continue;

		const file = normalise(match[1]);

		if (file === shimFile) api = true;
		if (inside(file, runtime)) continue;

		for (const extension of state.extensions) {
			if (inside(file, extension.directory)) return { extension, api };
		}
	}

	return null;
}

function matches(list, target, kind) {
	if (!Array.isArray(list)) return false;

	for (const entry of list) {
		if (kind === "read" || kind === "write") {
			if (inside(normalise(target), normalise(entry))) return true;
		} else if (String(entry).toLowerCase() === String(target).toLowerCase()) return true;
	}

	return false;
}

function scopeFor(kind, target) {
	if (kind !== "read" && kind !== "write") return target;

	const resolved = nodePath.resolve(target);
	const home = os.homedir();

	if (inside(normalise(resolved), normalise(home))) {
		const relative = nodePath.relative(home, resolved).split(nodePath.sep);
		return relative.length > 1 ? nodePath.join(home, relative[0]) : resolved;
	}

	const parts = resolved.split(nodePath.sep).filter(Boolean);
	return parts.length > 2 ? parts.slice(0, 2).join(nodePath.sep) + nodePath.sep : nodePath.dirname(resolved);
}

function isLoopback(host) {
	const value = String(host ?? "").toLowerCase().replace(/^\[|\]$/g, "");
	return value === "localhost" || value === "::1" || value.startsWith("127.") || value === "0.0.0.0" || value === "";
}

function defaultAllowed(extension, kind, target) {
	if (kind === "read" || kind === "write") {
		const file = normalise(target);

		if (inside(file, extension.directory)) return true;
		if (extension.storage.some(directory => inside(file, directory))) return true;
		if (inside(file, state.temp)) return true;
		if (kind === "read" && inside(file, normalise(runtimeDirectory))) return true;
		if (kind === "read" && state.workspaceFolders.some(folder => inside(file, folder))) return true;

		return false;
	}

	if (kind === "network") return isLoopback(target);

	return false;
}

function describe(kind, target) {
	switch (kind) {
		case "process": return `run ${nodePath.basename(String(target))}`;
		case "network": return `connect to ${target}`;
		case "native": return `load native module ${nodePath.basename(String(target))}`;
		case "read": return `read files in ${target}`;
		default: return `write files in ${target}`;
	}
}

function blocked(extension, kind, scope) {
	const error = new Error(`Forge blocked ${extension.name} from trying to ${describe(kind, scope)}`);
	error.code = "EACCES";
	error.errno = -13;
	error.forgeBlocked = true;
	return error;
}

function evaluate(kind, rawTarget) {
	if (rawTarget === null || rawTarget === undefined || state.depth > 0) return null;

	const caller = callerExtension();
	if (!caller) return null;

	const { extension, api } = caller;
	const target = kind === "read" || kind === "write" ? nodePath.resolve(String(rawTarget)) : String(rawTarget);

	if (defaultAllowed(extension, kind, target)) return null;
	if (api && kind === "write" && state.workspaceFolders.some(folder => inside(normalise(target), folder))) return null;
	if (matches(extension.grants[kind], target, kind)) return null;

	const scope = scopeFor(kind, target);

	if (matches(extension.denied[kind], target, kind)) throw blocked(extension, kind, scope);

	return { extension, kind, target: scope, detail: target, description: describe(kind, scope) };
}

function remember(pending, allowed) {
	if (allowed === null) return;

	const bucket = allowed ? pending.extension.grants : pending.extension.denied;
	(bucket[pending.kind] ??= []).push(pending.target);
}

function payloadOf(pending) {
	return { id: pending.extension.id, kind: pending.kind, target: pending.target, detail: pending.detail, description: pending.description };
}

function keyOf(pending) {
	return `${pending.extension.id}\u0000${pending.kind}\u0000${String(pending.target).toLowerCase()}`;
}

const sleeper = new Int32Array(new SharedArrayBuffer(4));

function openPipe() {
	for (let attempt = 0; attempt < 200; attempt++) {
		try {
			return originals.openSync(pipeName, "r+");
		} catch (error) {
			if (error?.code !== "ENOENT" && error?.code !== "EBUSY") return null;
			Atomics.wait(sleeper, 0, 0, 10);
		}
	}

	return null;
}

function decideSync(pending) {
	if (!pipeName) return false;

	let descriptor = null;

	try {
		descriptor = openPipe();
		if (descriptor === null) return false;

		originals.writeSync(descriptor, JSON.stringify(payloadOf(pending)) + "\n");

		const buffer = Buffer.alloc(1024);
		let text = "";

		while (!text.includes("\n")) {
			const read = originals.readSync(descriptor, buffer, 0, buffer.length, null);
			if (read <= 0) break;
			text += buffer.toString("utf8", 0, read);
		}

		const answer = JSON.parse(text.trim() || "{}");
		remember(pending, answer.remembered === false ? null : answer.allowed === true);
		return answer.allowed === true;
	} catch {
		return false;
	} finally {
		if (descriptor !== null) {
			try {
				originals.closeSync(descriptor);
			} catch {
			}
		}
	}
}

function decideAsync(pending) {
	if (!state.ask) return Promise.resolve(decideSync(pending));

	const key = keyOf(pending);
	let promise = state.inflight.get(key);

	if (!promise) {
		promise = Promise.resolve(state.ask(payloadOf(pending)))
			.then(answer => {
				remember(pending, answer?.remembered === false ? null : answer?.allowed === true);
				return answer?.allowed === true;
			})
			.catch(() => false)
			.finally(() => state.inflight.delete(key));

		state.inflight.set(key, promise);
	}

	return promise;
}

function gateSync(kind, target) {
	const pending = evaluate(kind, target);
	if (pending && !decideSync(pending)) throw blocked(pending.extension, pending.kind, pending.target);
}

async function gateAsync(checks) {
	for (const [kind, target] of checks) {
		const pending = evaluate(kind, target);
		if (pending && !(await decideAsync(pending))) throw blocked(pending.extension, pending.kind, pending.target);
	}
}

function mark(guarded, original) {
	Object.defineProperties(guarded, Object.getOwnPropertyDescriptors(original));
	guarded.__forgeGuarded = true;
	return guarded;
}

function install(object, name, guarded) {
	try {
		object[name] = guarded;
	} catch {
	}
}

function wrapSync(object, name, targets) {
	const original = object?.[name];
	if (typeof original !== "function" || original.__forgeGuarded) return;

	install(object, name, mark(function (...args) {
		for (const [kind, target] of targets(args)) gateSync(kind, target);
		return passThrough(this, original, args, new.target);
	}, original));
}

function needsDecision(checks) {
	return checks.some(([kind, target]) => evaluate(kind, target) !== null);
}

function wrapPromise(object, name, targets) {
	const original = object?.[name];
	if (typeof original !== "function" || original.__forgeGuarded) return;

	install(object, name, mark(function (...args) {
		const checks = targets(args);

		try {
			if (!needsDecision(checks)) return passThrough(this, original, args);
		} catch (error) {
			return Promise.reject(error);
		}

		return gateAsync(checks).then(() => passThrough(this, original, args));
	}, original));
}

function wrapCallback(object, name, targets) {
	const original = object?.[name];
	if (typeof original !== "function" || original.__forgeGuarded) return;

	install(object, name, mark(function (...args) {
		const callback = typeof args[args.length - 1] === "function" ? args[args.length - 1] : null;
		const checks = targets(args);

		if (!callback) {
			for (const [kind, target] of checks) gateSync(kind, target);
			return passThrough(this, original, args);
		}

		try {
			if (!needsDecision(checks)) return passThrough(this, original, args);
		} catch (error) {
			queueMicrotask(() => callback(error));
			return undefined;
		}

		gateAsync(checks).then(() => passThrough(this, original, args), error => callback(error));
		return undefined;
	}, original));
}

function writes(flags) {
	if (typeof flags === "number") {
		const constants = require("fs").constants;
		return (flags & (constants.O_WRONLY | constants.O_RDWR | constants.O_APPEND | constants.O_CREAT | constants.O_TRUNC)) !== 0;
	}

	return typeof flags === "string" && /[wa+]/.test(flags);
}

const writeFirst = ["writeFile", "appendFile", "mkdir", "rm", "rmdir", "unlink", "truncate", "chmod", "chown", "utimes", "lchown", "lutimes", "mkdtemp"];
const readFirst = ["readFile", "readdir", "opendir", "readlink"];
const twoPaths = ["rename", "copyFile", "cp", "symlink", "link"];

const one = kind => args => [[kind, toPath(args[0])]];
const two = name => args => [[name === "rename" ? "write" : "read", toPath(args[0])], ["write", toPath(args[1])]];
const opening = args => [[writes(args[1]) ? "write" : "read", toPath(args[0])]];

function patchFs(fs) {
	for (const name of writeFirst) {
		wrapCallback(fs, name, one("write"));
		wrapSync(fs, name + "Sync", one("write"));
	}

	for (const name of readFirst) {
		wrapCallback(fs, name, one("read"));
		wrapSync(fs, name + "Sync", one("read"));
	}

	for (const name of twoPaths) {
		wrapCallback(fs, name, two(name));
		wrapSync(fs, name + "Sync", two(name));
	}

	wrapCallback(fs, "open", opening);
	wrapSync(fs, "openSync", opening);
	wrapSync(fs, "createWriteStream", one("write"));
	wrapSync(fs, "createReadStream", one("read"));
}

function patchPromises(promises) {
	if (!promises) return;

	for (const name of writeFirst) wrapPromise(promises, name, one("write"));
	for (const name of readFirst) wrapPromise(promises, name, one("read"));
	for (const name of twoPaths) wrapPromise(promises, name, two(name));

	wrapPromise(promises, "open", opening);
}

function commandTarget(command) {
	const text = String(command ?? "").trim();
	const quoted = /^"([^"]+)"/.exec(text);
	return quoted ? quoted[1] : text.split(/\s+/)[0];
}

function patchChildProcess(childProcess) {
	for (const name of ["spawn", "spawnSync", "execFile", "execFileSync"]) wrapSync(childProcess, name, args => [["process", String(args[0])]]);
	for (const name of ["exec", "execSync"]) wrapSync(childProcess, name, args => [["process", commandTarget(args[0])]]);

	wrapSync(childProcess, "fork", args => [["process", toPath(args[0]) ?? "fork"]]);
}

function hostOf(input) {
	try {
		if (typeof input === "string") return new URL(input).hostname;
		if (input instanceof URL) return input.hostname;
		if (input && typeof input === "object") {
			if (typeof input.url === "string") return new URL(input.url).hostname;
			return input.hostname ?? input.host?.split(":")[0] ?? "localhost";
		}
	} catch {
	}

	return null;
}

function socketHost(args) {
	const first = args[0];
	if (first && typeof first === "object") return first.path ? "localhost" : first.host ?? first.servername ?? "localhost";
	return typeof first === "number" ? args[1] ?? "localhost" : "localhost";
}

function patchNetwork() {
	wrapPromise(globalThis, "fetch", args => [["network", hostOf(args[0])]]);

	if (typeof globalThis.WebSocket === "function") wrapSync(globalThis, "WebSocket", args => [["network", hostOf(args[0])]]);

	for (const moduleName of ["http", "https"]) {
		const module = require(moduleName);
		for (const name of ["request", "get"]) wrapSync(module, name, args => [["network", hostOf(args[0])]]);
	}

	const net = require("net");
	for (const name of ["connect", "createConnection"]) wrapSync(net, name, args => [["network", socketHost(args)]]);

	wrapSync(require("tls"), "connect", args => [["network", socketHost(args)]]);

	try {
		wrapSync(require("http2"), "connect", args => [["network", hostOf(args[0])]]);
	} catch {
	}

	wrapSync(require("dgram"), "createSocket", () => [["network", "udp"]]);
}

function patchBun() {
	if (typeof Bun === "undefined") return;

	for (const name of ["spawn", "spawnSync"]) {
		wrapSync(Bun, name, args => {
			const first = args[0];
			const command = Array.isArray(first) ? first[0] : Array.isArray(first?.cmd) ? first.cmd[0] : first;
			return [["process", String(command)]];
		});
	}

	wrapPromise(Bun, "write", args => [["write", toPath(args[0]?.name ?? args[0])]]);
	wrapSync(Bun, "file", args => [["read", toPath(args[0])]]);
	wrapSync(Bun, "connect", args => [["network", args[0]?.hostname ?? "localhost"]]);
}

function patchNative() {
	const Module = require("module");
	const loader = Module._extensions?.[".node"];

	if (typeof loader === "function" && !loader.__forgeGuarded) {
		Module._extensions[".node"] = mark(function (module, filename) {
			gateSync("native", filename);
			return passThrough(this, loader, [module, filename]);
		}, loader);
	}

	wrapSync(process, "dlopen", args => [["native", toPath(args[1])]]);
}

function snapshot() {
	return {
		extensions: state.extensions.map(extension => ({ id: extension.id, name: extension.name, directory: extension.directory, storage: extension.storage, grants: extension.grants, denied: extension.denied })),
		workspaceFolders: state.workspaceFolders,
	};
}

function patchWorkers(preloadPath) {
	const workerThreads = require("worker_threads");
	const Original = workerThreads.Worker;

	const optionsFor = options => ({
		...options,
		preload: [preloadPath, ...[].concat(options.preload ?? [])],
		env: { ...(options.env ?? process.env), FORGE_GUARD_CONFIG: JSON.stringify(snapshot()) },
	});

	if (typeof Original === "function" && !Original.__forgeGuarded) {
		class GuardedWorker extends Original {
			constructor(file, options = {}) {
				super(file, optionsFor(options));
			}
		}

		GuardedWorker.__forgeGuarded = true;
		workerThreads.Worker = GuardedWorker;
	}

	if (typeof globalThis.Worker === "function" && !globalThis.Worker.__forgeGuarded) {
		const WebWorker = globalThis.Worker;

		class GuardedWebWorker extends WebWorker {
			constructor(file, options = {}) {
				super(file, optionsFor(options));
			}
		}

		GuardedWebWorker.__forgeGuarded = true;
		globalThis.Worker = GuardedWebWorker;
	}
}

function start(options = {}) {
	state.ask = options.ask ?? null;

	const fs = require("fs");
	patchFs(fs);
	patchPromises(fs.promises);

	try {
		patchPromises(require("fs/promises"));
	} catch {
	}

	patchChildProcess(require("child_process"));
	patchNetwork();
	patchBun();
	patchNative();
	patchWorkers(__filename);
}

function update(config) {
	configure(config);
}

if (process.env.FORGE_GUARD_CONFIG && !require("worker_threads").isMainThread) {
	try {
		configure(JSON.parse(process.env.FORGE_GUARD_CONFIG));
	} catch {
	}

	start();
}

module.exports = { install: start, update };
