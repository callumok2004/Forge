const HEADER_END = "\r\n\r\n";

class Rpc {
	constructor(input, output) {
		this.output = output;
		this.pending = new Map();
		this.handlers = new Map();
		this.nextId = 1;
		this.buffer = Buffer.alloc(0);

		input.on("data", chunk => this.receive(chunk));
	}

	on(method, handler) {
		this.handlers.set(method, handler);
	}

	notify(method, params) {
		this.write({ jsonrpc: "2.0", method, params });
	}

	request(method, params) {
		const id = this.nextId++;
		const settled = Promise.withResolvers();
		this.pending.set(id, settled);
		this.write({ jsonrpc: "2.0", id, method, params });
		return settled.promise;
	}

	write(message) {
		const body = Buffer.from(JSON.stringify(message), "utf8");
		this.output.write(`Content-Length: ${body.length}${HEADER_END}`);
		this.output.write(body);
	}

	receive(chunk) {
		this.buffer = Buffer.concat([this.buffer, chunk]);

		for (;;) {
			const split = this.buffer.indexOf(HEADER_END);
			if (split < 0) return;

			const header = this.buffer.subarray(0, split).toString("ascii");
			const match = /content-length:\s*(\d+)/i.exec(header);
			if (!match) {
				this.buffer = this.buffer.subarray(split + HEADER_END.length);
				continue;
			}

			const length = Number(match[1]);
			const start = split + HEADER_END.length;
			if (this.buffer.length < start + length) return;

			const body = this.buffer.subarray(start, start + length).toString("utf8");
			this.buffer = this.buffer.subarray(start + length);

			let message;
			try {
				message = JSON.parse(body);
			} catch {
				continue;
			}

			this.dispatch(message);
		}
	}

	async dispatch(message) {
		if (message.id !== undefined && message.method === undefined) {
			const settled = this.pending.get(message.id);
			if (!settled) return;
			this.pending.delete(message.id);
			if (message.error) settled.reject(new Error(message.error.message ?? "request failed"));
			else settled.resolve(message.result);
			return;
		}

		const handler = this.handlers.get(message.method);
		if (!handler) {
			if (message.id !== undefined) {
				this.write({ jsonrpc: "2.0", id: message.id, error: { code: -32601, message: `no handler for ${message.method}` } });
			}
			return;
		}

		try {
			const result = await handler(message.params);
			if (message.id !== undefined) this.write({ jsonrpc: "2.0", id: message.id, result: result ?? null });
		} catch (error) {
			if (message.id !== undefined) {
				this.write({ jsonrpc: "2.0", id: message.id, error: { code: -32000, message: String(error?.message ?? error) } });
			}
		}
	}
}

module.exports = { Rpc };
