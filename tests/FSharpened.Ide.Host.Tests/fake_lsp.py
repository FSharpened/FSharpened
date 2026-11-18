#!/usr/bin/env python3
"""A scripted language server for the tests and screenshots of FSharpened.Ide.Host and the desktop IDE.

It answers `initialize`, tracks open documents, and implements `prepareRename` and `rename` over the whole folder: the
identifier under the cursor is found in every file with the same extension (open buffers first, then the disk) on word
boundaries. It is not a language server for any language; it exists so rename can be driven end to end without one.
The words `fn`, `let`, `mod` and `pub` are refused, with a reason, as a real server refuses keywords.
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
        send({"id": mid, "result": {"capabilities": {"textDocumentSync": 1, "renameProvider": {"prepareProvider": True}}}})
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
    elif method == "shutdown":
        send({"id": mid, "result": None})
    elif method == "exit":
        break
    elif mid is not None:
        send({"id": mid, "result": None})
