// A stand-in for `dotnet fantomas --daemon`: the same JSON-RPC messages, a trivial "formatter" (it trims trailing
// whitespace). Used by the node tests so they run without a .NET SDK. Special inputs: SYNTAX_ERROR answers Error,
// HANG never answers, CRASH exits.
import { createMessageConnection, StreamMessageReader, StreamMessageWriter } from "vscode-jsonrpc/node.js";

if (!process.argv.includes("--daemon")) {
  console.error("expected --daemon");
  process.exit(2);
}

const connection = createMessageConnection(new StreamMessageReader(process.stdin), new StreamMessageWriter(process.stdout));
connection.onRequest("fantomas/version", () => "0.0.0-fake");
connection.onRequest("fantomas/formatDocument", (p) => {
  const source = p.SourceCode;
  if (source.includes("CRASH")) process.exit(3);
  if (source.includes("HANG")) return new Promise(() => {});
  if (source.includes("SYNTAX_ERROR")) return { Case: "Error", Fields: [p.FilePath, 'ParseException\n  [{ Severity = Error\n     Message =\n      "Unexpected syntax" }]'] };
  const formatted = source.replace(/[ \t]+$/gm, "");
  if (formatted === source) return { Case: "Unchanged", Fields: [p.FilePath] };
  return { Case: "Formatted", Fields: [p.FilePath, formatted, null] };
});
connection.listen();
