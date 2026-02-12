#!/usr/bin/env node

/*
  Minimal MCP (JSON-RPC over stdin/stdout) server that bridges to an LSP server (csharp-ls).

  Goals:
  - Make LSP usable inside Codex via MCP without external adapters.
  - Keep protocol surface intentionally small and explicit.

  Transport:
  - MCP: newline-delimited JSON-RPC messages (Codex-friendly).
  - LSP: Content-Length framed JSON-RPC over stdio.
*/

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const readline = require('readline');
const { pathToFileURL } = require('url');

const JSONRPC_VERSION = '2.0';
const MCP_PROTOCOL_VERSION = '2025-06-18';

function writeJsonLine(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

function nowIso() {
  return new Date().toISOString();
}

function asError(code, message, data) {
  const err = { code, message };
  if (data !== undefined) err.data = data;
  return err;
}

function toToolResult(ok, summaryText, structuredContent) {
  return {
    content: [{ type: 'text', text: summaryText }],
    structuredContent,
    isError: !ok,
  };
}

function normalizePath(p) {
  return path.resolve(p);
}

function fileUri(p) {
  return pathToFileURL(normalizePath(p)).toString();
}

class LspFramedReader {
  constructor(onMessage) {
    this._buffer = Buffer.alloc(0);
    this._onMessage = onMessage;
  }

  push(chunk) {
    if (!chunk || chunk.length === 0) return;
    this._buffer = Buffer.concat([this._buffer, chunk]);

    // Parse as many framed messages as possible.
    while (true) {
      const headerEnd = this._buffer.indexOf('\r\n\r\n');
      if (headerEnd < 0) return;

      const header = this._buffer.slice(0, headerEnd).toString('ascii');
      const lines = header.split(/\r\n/);
      let contentLength = null;
      for (const line of lines) {
        const idx = line.indexOf(':');
        if (idx < 0) continue;
        const name = line.slice(0, idx).trim().toLowerCase();
        const value = line.slice(idx + 1).trim();
        if (name === 'content-length') {
          const parsed = parseInt(value, 10);
          if (!Number.isFinite(parsed) || parsed < 0) {
            throw new Error(`Invalid Content-Length: ${value}`);
          }
          contentLength = parsed;
        }
      }
      if (contentLength === null) {
        throw new Error('Missing Content-Length header from LSP server.');
      }

      const messageStart = headerEnd + 4;
      const messageEnd = messageStart + contentLength;
      if (this._buffer.length < messageEnd) return;

      const body = this._buffer.slice(messageStart, messageEnd).toString('utf8');
      this._buffer = this._buffer.slice(messageEnd);

      let obj;
      try {
        obj = JSON.parse(body);
      } catch (e) {
        // Ignore malformed body but continue parsing.
        continue;
      }
      this._onMessage(obj);
    }
  }
}

class LspClient {
  constructor(command, args, cwd) {
    this._command = command;
    this._args = args;
    this._cwd = cwd;

    this._proc = null;
    this._reader = null;
    this._nextId = 1;
    this._pending = new Map();

    this._initialized = false;
    this._rootDir = null;
    this._openDocs = new Set();
    this._latestDiagnosticsByUri = new Map();
    this._serverInfo = null;
  }

  start() {
    if (this._proc) return;

    this._proc = spawn(this._command, this._args, {
      cwd: this._cwd,
      stdio: ['pipe', 'pipe', 'pipe'],
      windowsHide: true,
      env: process.env,
    });

    this._reader = new LspFramedReader((msg) => this._onMessage(msg));

    this._proc.stdout.on('data', (chunk) => this._reader.push(chunk));
    this._proc.stderr.on('data', (chunk) => {
      // stderr is useful for debugging but is not part of LSP json-rpc.
      // We intentionally do not forward it to MCP unless requested.
    });

    this._proc.on('exit', (code) => {
      for (const [id, pending] of this._pending.entries()) {
        pending.reject(new Error(`LSP server exited (code=${code}) before replying.`));
      }
      this._pending.clear();
      this._proc = null;
      this._initialized = false;
    });
  }

  _sendRaw(obj) {
    const json = JSON.stringify(obj);
    const bytes = Buffer.from(json, 'utf8');
    const header = Buffer.from(`Content-Length: ${bytes.length}\r\n\r\n`, 'ascii');
    this._proc.stdin.write(header);
    this._proc.stdin.write(bytes);
  }

  request(method, params, timeoutMs = 30000) {
    this.start();

    const id = this._nextId++;
    const req = { jsonrpc: JSONRPC_VERSION, id, method, params };

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(new Error(`LSP request timed out after ${timeoutMs}ms: ${method}`));
      }, timeoutMs);

      this._pending.set(id, {
        resolve: (v) => {
          clearTimeout(timer);
          resolve(v);
        },
        reject: (e) => {
          clearTimeout(timer);
          reject(e);
        },
      });

      this._sendRaw(req);
    });
  }

  notify(method, params) {
    this.start();
    const msg = { jsonrpc: JSONRPC_VERSION, method, params };
    this._sendRaw(msg);
  }

  _onMessage(msg) {
    if (!msg || typeof msg !== 'object') return;

    // Response
    if (Object.prototype.hasOwnProperty.call(msg, 'id')) {
      const pending = this._pending.get(msg.id);
      if (pending) {
        this._pending.delete(msg.id);
        if (msg.error) {
          pending.reject(new Error(`LSP error: ${JSON.stringify(msg.error)}`));
        } else {
          pending.resolve(msg.result);
        }
      }
      return;
    }

    // Notifications
    if (msg.method === 'textDocument/publishDiagnostics' && msg.params) {
      const uri = msg.params.uri;
      if (typeof uri === 'string') {
        this._latestDiagnosticsByUri.set(uri, msg.params);
      }
    }
  }

  async ensureInitialized(rootDir) {
    if (this._initialized) return;

    this._rootDir = rootDir;
    const rootUri = fileUri(rootDir);

    const result = await this.request('initialize', {
      processId: process.pid,
      rootUri,
      rootPath: rootDir,
      workspaceFolders: [{ uri: rootUri, name: path.basename(rootDir) }],
      capabilities: {
        textDocument: {
          synchronization: {
            didSave: true,
          },
        },
      },
      clientInfo: { name: 'roslynskills-csharp-lsp-mcp', version: '0.1' },
    }, 120000);

    this._serverInfo = (result && result.serverInfo) ? result.serverInfo : null;

    this.notify('initialized', {});
    this._initialized = true;
  }

  async ensureDidOpen(filePath) {
    const abs = normalizePath(filePath);
    if (this._openDocs.has(abs)) return;

    const text = fs.readFileSync(abs, 'utf8');
    const uri = fileUri(abs);

    // Best-effort language id.
    const languageId = abs.toLowerCase().endsWith('.csx') ? 'csharp' : 'csharp';

    this.notify('textDocument/didOpen', {
      textDocument: {
        uri,
        languageId,
        version: 1,
        text,
      },
    });

    this._openDocs.add(abs);
  }

  getLatestDiagnostics(filePath) {
    const uri = fileUri(filePath);
    return this._latestDiagnosticsByUri.get(uri) || null;
  }
}

const lsp = new LspClient(
  process.env.ROSLYNSKILLS_LSP_COMMAND || 'csharp-ls',
  process.env.ROSLYNSKILLS_LSP_ARGS ? JSON.parse(process.env.ROSLYNSKILLS_LSP_ARGS) : [],
  process.cwd()
);

const tools = [
  {
    name: 'csharp_lsp_request',
    description: 'Send a raw JSON-RPC request to the underlying LSP server.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['method'],
      properties: {
        method: { type: 'string' },
        params: { type: ['object', 'array', 'null'] },
        timeout_ms: { type: 'integer', minimum: 1, maximum: 300000 },
        root_dir: { type: 'string', description: 'Workspace root directory for initialize (defaults to current working directory).' },
      },
    },
  },
  {
    name: 'csharp_lsp_definition',
    description: 'Go to definition for symbol at a file/line/column using LSP.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['file_path', 'line', 'column'],
      properties: {
        file_path: { type: 'string' },
        line: { type: 'integer', minimum: 1, maximum: 200000 },
        column: { type: 'integer', minimum: 1, maximum: 200000 },
        root_dir: { type: 'string' },
        timeout_ms: { type: 'integer', minimum: 1, maximum: 300000 },
      },
    },
  },
  {
    name: 'csharp_lsp_references',
    description: 'Find references for symbol at a file/line/column using LSP.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['file_path', 'line', 'column'],
      properties: {
        file_path: { type: 'string' },
        line: { type: 'integer', minimum: 1, maximum: 200000 },
        column: { type: 'integer', minimum: 1, maximum: 200000 },
        include_declaration: { type: 'boolean' },
        root_dir: { type: 'string' },
        timeout_ms: { type: 'integer', minimum: 1, maximum: 300000 },
      },
    },
  },
  {
    name: 'csharp_lsp_hover',
    description: 'Get hover info for symbol at a file/line/column using LSP.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['file_path', 'line', 'column'],
      properties: {
        file_path: { type: 'string' },
        line: { type: 'integer', minimum: 1, maximum: 200000 },
        column: { type: 'integer', minimum: 1, maximum: 200000 },
        root_dir: { type: 'string' },
        timeout_ms: { type: 'integer', minimum: 1, maximum: 300000 },
      },
    },
  },
  {
    name: 'csharp_lsp_latest_diagnostics',
    description: 'Return latest pushed diagnostics (textDocument/publishDiagnostics) for a file, if the server supports it.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      required: ['file_path'],
      properties: {
        file_path: { type: 'string' },
        root_dir: { type: 'string' },
      },
    },
  },
];

async function handleToolCall(name, args) {
  const rootDirRequested = (args && typeof args.root_dir === 'string' && args.root_dir.trim())
    ? normalizePath(args.root_dir.trim())
    : null;

  let rootDirEffective = process.cwd();
  let rootDirValid = true;
  if (rootDirRequested) {
    try {
      rootDirValid = fs.existsSync(rootDirRequested);
    } catch {
      rootDirValid = false;
    }

    if (rootDirValid) {
      rootDirEffective = rootDirRequested;
    } else {
      rootDirEffective = process.cwd();
    }
  }

  await lsp.ensureInitialized(rootDirEffective);

  if (name === 'csharp_lsp_request') {
    const method = args.method;
    const params = Object.prototype.hasOwnProperty.call(args, 'params') ? args.params : null;
    const timeoutMs = (args.timeout_ms && Number.isFinite(args.timeout_ms)) ? args.timeout_ms : 120000;
    const result = await lsp.request(method, params, timeoutMs);
    return toToolResult(true, `csharp_lsp_request ok: ${method}`, { method, params, root_dir_requested: rootDirRequested, root_dir_effective: rootDirEffective, root_dir_valid: rootDirValid, result: (result === undefined ? null : result) });
  }

  if (name === 'csharp_lsp_latest_diagnostics') {
    const filePath = normalizePath(args.file_path);
    await lsp.ensureDidOpen(filePath);
    const latest = lsp.getLatestDiagnostics(filePath);
    const ok = latest !== null;
    const summary = ok
      ? `csharp_lsp_latest_diagnostics ok: ${path.basename(filePath)} (${(latest.diagnostics || []).length} item(s))`
      : `csharp_lsp_latest_diagnostics: no pushed diagnostics observed yet for ${path.basename(filePath)}`;
    return toToolResult(true, summary, { file_path: filePath, root_dir_requested: rootDirRequested, root_dir_effective: rootDirEffective, root_dir_valid: rootDirValid, diagnostics: latest });
  }

  const filePath = normalizePath(args.file_path);
  await lsp.ensureDidOpen(filePath);
  const uri = fileUri(filePath);
  const line0 = Math.max(0, (args.line | 0) - 1);
  const col0 = Math.max(0, (args.column | 0) - 1);
  const timeoutMs = (args.timeout_ms && Number.isFinite(args.timeout_ms)) ? args.timeout_ms : 120000;

  if (name === 'csharp_lsp_definition') {
    const result = await lsp.request('textDocument/definition', {
      textDocument: { uri },
      position: { line: line0, character: col0 },
    }, timeoutMs);
    return toToolResult(true, `csharp_lsp_definition ok: ${path.basename(filePath)}:${args.line}:${args.column}`, { file_path: filePath, root_dir_requested: rootDirRequested, root_dir_effective: rootDirEffective, root_dir_valid: rootDirValid, result: (result === undefined ? null : result) });
  }

  if (name === 'csharp_lsp_references') {
    const includeDeclaration = !!args.include_declaration;
    const result = await lsp.request('textDocument/references', {
      textDocument: { uri },
      position: { line: line0, character: col0 },
      context: { includeDeclaration },
    }, timeoutMs);
    return toToolResult(true, `csharp_lsp_references ok: ${path.basename(filePath)}:${args.line}:${args.column}`, { file_path: filePath, root_dir_requested: rootDirRequested, root_dir_effective: rootDirEffective, root_dir_valid: rootDirValid, result: (result === undefined ? null : result) });
  }

  if (name === 'csharp_lsp_hover') {
    const result = await lsp.request('textDocument/hover', {
      textDocument: { uri },
      position: { line: line0, character: col0 },
    }, timeoutMs);
    return toToolResult(true, `csharp_lsp_hover ok: ${path.basename(filePath)}:${args.line}:${args.column}`, { file_path: filePath, root_dir_requested: rootDirRequested, root_dir_effective: rootDirEffective, root_dir_valid: rootDirValid, result: (result === undefined ? null : result) });
  }

  throw new Error(`Unknown tool: ${name}`);
}

function buildToolsList() {
  return { tools };
}

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

rl.on('line', async (line) => {
  if (!line || !line.trim()) return;

  let msg;
  try {
    msg = JSON.parse(line);
  } catch (e) {
    writeJsonLine({ jsonrpc: JSONRPC_VERSION, id: null, error: asError(-32700, 'Parse error.', String(e && e.message ? e.message : e)) });
    return;
  }

  if (!msg || msg.jsonrpc !== JSONRPC_VERSION || typeof msg.method !== 'string') {
    if (Object.prototype.hasOwnProperty.call(msg, 'id')) {
      writeJsonLine({ jsonrpc: JSONRPC_VERSION, id: msg.id ?? null, error: asError(-32600, 'Invalid request.', null) });
    }
    return;
  }

  const id = Object.prototype.hasOwnProperty.call(msg, 'id') ? msg.id : undefined;
  const params = msg.params ?? {};

  try {
    switch (msg.method) {
      case 'initialize': {
        if (id === undefined) return;
        const result = {
          protocolVersion: MCP_PROTOCOL_VERSION,
          capabilities: {
            tools: { listChanged: false },
            resources: { subscribe: false, listChanged: false },
          },
          serverInfo: {
            name: 'roslynskills-csharp-lsp-mcp',
            version: '0.1',
            startTimeUtc: nowIso(),
          },
        };
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result });
        return;
      }
      case 'tools/list': {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result: buildToolsList() });
        return;
      }
      case 'tools/call': {
        if (id === undefined) return;
        if (!params || typeof params !== 'object') {
          writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, error: asError(-32602, 'Invalid params: tools/call expects an object.', null) });
          return;
        }
        const name = params.name;
        if (typeof name !== 'string' || !name.trim()) {
          writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, error: asError(-32602, 'Invalid params: tools/call requires a non-empty tool name.', null) });
          return;
        }
        const args = params.arguments ?? {};

        const toolResult = await handleToolCall(name.trim(), args);
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result: toolResult });
        return;
      }
      case 'resources/list': {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result: { resources: [] } });
        return;
      }
      case 'resources/templates/list': {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result: { resourceTemplates: [] } });
        return;
      }
      case 'resources/read': {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, error: asError(-32601, 'No resources are exposed by this server.', null) });
        return;
      }
      case 'ping': {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, result: {} });
        return;
      }
      default: {
        if (id === undefined) return;
        writeJsonLine({ jsonrpc: JSONRPC_VERSION, id, error: asError(-32601, `Method not found: ${msg.method}`, null) });
        return;
      }
    }
  } catch (e) {
    if (id === undefined) return;
    writeJsonLine({
      jsonrpc: JSONRPC_VERSION,
      id,
      result: toToolResult(false, `error: ${e && e.message ? e.message : String(e)}`, { error: String(e && e.stack ? e.stack : e) }),
    });
  }
});