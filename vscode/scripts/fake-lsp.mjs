// A stand-in language server for the VS Code smoke test: speaks just enough LSP over stdio to be started by the
// extension's language client, and records every notification it receives to the file named by FAKE_LSP_LOG.
// It is NOT FsAutoComplete and proves nothing about F# intelligence: it proves the client launches a server,
// completes the initialize handshake and sends textDocument/didOpen for an F# file.
import { appendFileSync } from "node:fs";
import { createMessageConnection, StreamMessageReader, StreamMessageWriter } from "vscode-jsonrpc/node.js";

const log = process.env.FAKE_LSP_LOG;
const note = (line) => {
  if (log) appendFileSync(log, line + "\n");
};

const connection = createMessageConnection(new StreamMessageReader(process.stdin), new StreamMessageWriter(process.stdout));
connection.onRequest("initialize", (p) => {
  note(`initialize rootUri=${p.rootUri} initializationOptions=${JSON.stringify(p.initializationOptions)}`);
  return { capabilities: { textDocumentSync: 1 }, serverInfo: { name: "fake-lsp" } };
});
connection.onNotification("initialized", () => note("initialized"));
connection.onNotification("textDocument/didOpen", (p) => note(`didOpen ${p.textDocument.uri} languageId=${p.textDocument.languageId}`));
connection.onRequest("shutdown", () => {
  note("shutdown");
  return null;
});
connection.onNotification("exit", () => process.exit(0));
connection.listen();
