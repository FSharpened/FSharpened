#!/usr/bin/env python3
"""A scripted language server for the tests and screenshots of FSharpened.Ide.Host and the desktop IDE.

It answers `initialize`, tracks open documents, and implements `prepareRename` and `rename` over the whole folder: the
identifier under the cursor is found in every file with the same extension (open buffers first, then the disk) on word
boundaries. It is not a language server for any language; it exists so rename can be driven end to end without one.
The words `fn`, `let`, `mod` and `pub` are refused, with a reason, as a real server refuses keywords.

It also answers, from plain text rules, the editor requests the desktop IDE makes (all switched off by `--basic`, so a
server without them can be tested too):
  references         every whole-word occurrence of the word under the cursor, in open buffers and files of the folder
  documentSymbol     `fn name(...)`, `func name(...)`, `class Name`, `struct Name`; a `    fn` inside a class is its child
  signatureHelp      inside `name(` of a `fn name(a, b)` declared in the buffer: the signature and the active parameter
  codeAction         "Remove trailing whitespace" (quickfix, with an edit), "Convert word to upper case" (refactor, resolved
                     later by codeAction/resolve) and "Run server command" (a bare command)
  formatting         strips trailing whitespace and adds a final newline when there is none
"""
import json
import os
import re
import sys
from urllib.parse import quote, unquote, urlparse
from urllib.request import url2pathname

stdin = sys.stdin.buffer
stdout = sys.stdout.buffer
docs = {}
root = ""
KEYWORDS = {"fn", "let", "mod", "pub"}
BASIC = "--basic" in sys.argv
NL = chr(10)


def read():
    length = None
    while True:
        line = stdin.readline()
        if not line:
            return None
        line = line.strip()
        if line == b"":
            break
        if line.lower().startswith(b"content-length:"):
            length = int(line.split(b":")[1])
    return json.loads(stdin.read(length).decode("utf-8")) if length is not None else None


def send(message):
    message["jsonrpc"] = "2.0"
    body = json.dumps(message).encode("utf-8")
    stdout.write(b"Content-Length: %d\r\n\r\n" % len(body))
    stdout.write(body)
    stdout.flush()


def path_of(uri):
    return url2pathname(unquote(urlparse(uri).path))


def uri_of(path):
    return "file:///" + quote(os.path.abspath(path).replace("\\", "/").lstrip("/"), safe="/:")


def text_of(uri):
    if uri in docs:
        return docs[uri]
    with open(path_of(uri), encoding="utf-8") as f:
        return f.read()


def word_at(text, line, character):
    lines = text.split("\n")
    if line >= len(lines):
        return None
    s = lines[line].rstrip("\r")
    a = b = min(character, len(s))
    while a > 0 and (s[a - 1].isalnum() or s[a - 1] == "_"):
        a -= 1
    while b < len(s) and (s[b].isalnum() or s[b] == "_"):
        b += 1
    return (s[a:b], a, b) if b > a else None


def files_like(uri):
    ext = os.path.splitext(path_of(uri))[1]
    found = {}
    for d in [root] if root else []:
        for base, dirs, names in os.walk(d):
            dirs[:] = [x for x in dirs if x not in ("target", ".git", "node_modules")]
            for n in names:
                if n.endswith(ext):
                    found[uri_of(os.path.join(base, n))] = True
    for u in docs:
        found[u] = True
    return list(found)


while True:
    m = read()
    if m is None:
        break
    method = m.get("method")
    mid = m.get("id")
    p = m.get("params", {})
    if method == "initialize":
        root = path_of(p.get("rootUri", "")) if p.get("rootUri") else ""
        caps = {"textDocumentSync": 1, "renameProvider": {"prepareProvider": True}}
        if not BASIC:
            caps.update({
                "referencesProvider": True,
                "documentSymbolProvider": True,
                "signatureHelpProvider": {"triggerCharacters": ["(", ","]},
                "codeActionProvider": {"resolveProvider": True},
                "documentFormattingProvider": True,
            })
        send({"id": mid, "result": {"capabilities": caps}})
    elif method == "textDocument/didOpen":
        d = p["textDocument"]
        docs[d["uri"]] = d["text"]
    elif method == "textDocument/didChange":
        docs[p["textDocument"]["uri"]] = p["contentChanges"][-1]["text"]
    elif method == "textDocument/didClose":
        docs.pop(p["textDocument"]["uri"], None)
    elif method == "textDocument/prepareRename":
        uri = p["textDocument"]["uri"]
        w = word_at(text_of(uri), p["position"]["line"], p["position"]["character"])
        if w is None:
            send({"id": mid, "result": None})
        elif w[0] in KEYWORDS:
            send({"id": mid, "error": {"code": -32600, "message": "cannot rename the keyword '%s'" % w[0]}})
        else:
            line = p["position"]["line"]
            send({"id": mid, "result": {"range": {"start": {"line": line, "character": w[1]}, "end": {"line": line, "character": w[2]}}, "placeholder": w[0]}})
    elif method == "textDocument/rename":
        uri = p["textDocument"]["uri"]
        w = word_at(text_of(uri), p["position"]["line"], p["position"]["character"])
        if w is None:
            send({"id": mid, "error": {"code": -32600, "message": "nothing to rename here"}})
            continue
        pattern = re.compile(r"\b%s\b" % re.escape(w[0]))
        changes = []
        for u in files_like(uri):
            edits = []
            for i, line in enumerate(text_of(u).split("\n")):
                for hit in pattern.finditer(line.rstrip("\r")):
                    edits.append({"range": {"start": {"line": i, "character": hit.start()}, "end": {"line": i, "character": hit.end()}}, "newText": p["newName"]})
            if edits:
                changes.append({"textDocument": {"uri": u, "version": None}, "edits": edits})
        send({"id": mid, "result": {"documentChanges": changes}})
    elif BASIC and method in ("textDocument/references", "textDocument/documentSymbol", "textDocument/signatureHelp", "textDocument/codeAction", "textDocument/formatting"):
        send({"id": mid, "error": {"code": -32601, "message": "method not found"}})
    elif method == "textDocument/references":
        uri = p["textDocument"]["uri"]
        w = word_at(text_of(uri), p["position"]["line"], p["position"]["character"])
        found = []
        if w is not None:
            pattern = re.compile(r"\b%s\b" % re.escape(w[0]))
            for u in files_like(uri):
                for i, line in enumerate(text_of(u).split("\n")):
                    for hit in pattern.finditer(line.rstrip("\r")):
                        found.append({"uri": u, "range": {"start": {"line": i, "character": hit.start()}, "end": {"line": i, "character": hit.end()}}})
        send({"id": mid, "result": found or None})
    elif method == "textDocument/documentSymbol":
        text = text_of(p["textDocument"]["uri"])
        top = []
        current = None
        for i, raw in enumerate(text.split("\n")):
            line = raw.rstrip("\r")
            m = re.match(r"^(\s*)(fn|func|class|struct)\s+(\w+)", line)
            if not m:
                continue
            kind = {"fn": 12, "func": 12, "class": 5, "struct": 23}[m.group(2)]
            a = m.start(3)
            sym = {"name": m.group(3), "kind": kind, "range": {"start": {"line": i, "character": 0}, "end": {"line": i, "character": len(line)}},
                   "selectionRange": {"start": {"line": i, "character": a}, "end": {"line": i, "character": a + len(m.group(3))}}}
            if m.group(1) and current is not None:
                sym["kind"] = 6
                current.setdefault("children", []).append(sym)
                current["range"]["end"] = sym["range"]["end"]
            else:
                top.append(sym)
                current = sym if kind in (5, 23) else None
        send({"id": mid, "result": top})
    elif method == "textDocument/signatureHelp":
        text = text_of(p["textDocument"]["uri"])
        lines = text.split("\n")
        ln, ch = p["position"]["line"], p["position"]["character"]
        before = "\n".join(lines[:ln] + [lines[ln][:ch]]) if ln < len(lines) else ""
        depth = 0
        opener = None
        for k in range(len(before) - 1, -1, -1):
            c = before[k]
            if c == ")":
                depth += 1
            elif c == "(":
                if depth == 0:
                    opener = k
                    break
                depth -= 1
        result = None
        if opener is not None:
            m = re.search(r"(\w+)$", before[:opener])
            decl = re.search(r"\bfn\s+%s\(([^)]*)\)" % re.escape(m.group(1)), text) if m else None
            if decl:
                params = [x.strip() for x in decl.group(1).split(",") if x.strip()]
                label = "%s(%s)" % (m.group(1), ", ".join(params))
                start = len(m.group(1)) + 1
                infos = []
                for x in params:
                    infos.append({"label": [start, start + len(x)], "documentation": "the %s" % x.split(":")[0]})
                    start += len(x) + 2
                result = {"signatures": [{"label": label, "documentation": {"kind": "markdown", "value": "Declared in this file."}, "parameters": infos}],
                          "activeSignature": 0, "activeParameter": before[opener:].count(",")}
        send({"id": mid, "result": result})
    elif method == "textDocument/codeAction":
        uri = p["textDocument"]["uri"]
        lines = text_of(uri).split("\n")
        first, last = p["range"]["start"]["line"], p["range"]["end"]["line"]
        actions = []
        for i in range(first, min(last, len(lines) - 1) + 1):
            line = lines[i].rstrip("\r")
            stripped = line.rstrip()
            if stripped != line:
                edit = {"changes": {uri: [{"range": {"start": {"line": i, "character": len(stripped)}, "end": {"line": i, "character": len(line)}}, "newText": ""}]}}
                actions.append({"title": "Remove trailing whitespace", "kind": "quickfix", "isPreferred": True, "edit": edit,
                                "diagnostics": p.get("context", {}).get("diagnostics", [])})
        w = word_at(text_of(uri), first, p["range"]["start"]["character"])
        if w is not None:
            actions.append({"title": "Convert '%s' to upper case" % w[0], "kind": "refactor", "data": {"uri": uri, "line": first, "a": w[1], "b": w[2], "to": w[0].upper()}})
        actions.append({"title": "Run server command", "command": "fake.run", "arguments": []})
        send({"id": mid, "result": actions})
    elif method == "codeAction/resolve":
        d = p["data"]
        p["edit"] = {"changes": {d["uri"]: [{"range": {"start": {"line": d["line"], "character": d["a"]}, "end": {"line": d["line"], "character": d["b"]}}, "newText": d["to"]}]}}
        send({"id": mid, "result": p})
    elif method == "textDocument/formatting":
        text = text_of(p["textDocument"]["uri"])
        edits = []
        lines = text.split("\n")
        for i, raw in enumerate(lines):
            line = raw.rstrip("\r")
            if line != line.rstrip():
                edits.append({"range": {"start": {"line": i, "character": len(line.rstrip())}, "end": {"line": i, "character": len(line)}}, "newText": ""})
        if not text.endswith(NL):
            last = len(lines) - 1
            end = len(lines[last])
            edits.append({"range": {"start": {"line": last, "character": end}, "end": {"line": last, "character": end}}, "newText": NL})
        send({"id": mid, "result": edits})
    elif method == "shutdown":
        send({"id": mid, "result": None})
    elif method == "exit":
        break
    elif mid is not None:
        send({"id": mid, "result": None})
