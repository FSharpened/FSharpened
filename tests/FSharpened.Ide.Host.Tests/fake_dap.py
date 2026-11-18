#!/usr/bin/env python3
"""A scripted Debug Adapter Protocol server for the tests of FSharpened.Ide.Host.

It speaks DAP over stdio, or over one local TCP connection with `--port N`, and behaves like a small real adapter:
`launch` is answered only after `configurationDone` (as debugpy does), the debuggee "stops" at the first breakpoint it
was given, `next` moves one line, and `continue` ends the program. It also sends the client a `runInTerminal` request
once, to check that the client refuses what it does not support.

  --fail-launch   answer `launch` with an error
  --port N        listen on 127.0.0.1:N instead of using stdio
"""
import json
import socket
import sys

FAIL_LAUNCH = "--fail-launch" in sys.argv
PORT = None
if "--port" in sys.argv:
    PORT = int(sys.argv[sys.argv.index("--port") + 1])

if PORT is not None:
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", PORT))
    server.listen(1)
    conn, _ = server.accept()
    rfile = conn.makefile("rb")
    wfile = conn.makefile("wb")
else:
    rfile = sys.stdin.buffer
    wfile = sys.stdout.buffer

seq = 0


def send(message):
    global seq
    seq += 1
    message["seq"] = seq
    body = json.dumps(message).encode("utf-8")
    wfile.write(b"Content-Length: %d\r\n\r\n" % len(body))
    wfile.write(body)
    wfile.flush()


def read():
    length = None
    while True:
        line = rfile.readline()
        if not line:
            return None
        line = line.strip()
        if line == b"":
            break
        if line.lower().startswith(b"content-length:"):
            length = int(line.split(b":")[1])
    if length is None:
        return None
    return json.loads(rfile.read(length).decode("utf-8"))


def respond(req, body=None, ok=True, message=None):
    r = {"type": "response", "request_seq": req["seq"], "success": ok, "command": req["command"]}
    if body is not None:
        r["body"] = body
    if message is not None:
        r["message"] = message
    send(r)


def event(name, body=None):
    e = {"type": "event", "event": name}
    if body is not None:
        e["body"] = body
    send(e)


breakpoints = {}  # path -> [lines]
launch_request = None
current = {"path": "", "line": 0}
variables = {
    10: [
        {"name": "x", "value": "1", "type": "int", "variablesReference": 0},
        {"name": "obj", "value": "Point", "type": "Point", "variablesReference": 20},
    ],
    11: [{"name": "__name__", "value": "'__main__'", "type": "str", "variablesReference": 0}],
    20: [
        {"name": "X", "value": "3", "type": "int", "variablesReference": 0},
        {"name": "Y", "value": "4", "type": "int", "variablesReference": 0},
    ],
}


def stop(reason):
    event("stopped", {"reason": reason, "threadId": 1, "allThreadsStopped": True, "description": "Paused on " + reason})


while True:
    req = read()
    if req is None:
        break
    if req.get("type") == "response":
        if not req.get("success"):
            event("output", {"category": "console", "output": "runInTerminal refused: %s\n" % req.get("message", "")})
        continue
    cmd = req.get("command")
    args = req.get("arguments", {})
    if cmd == "initialize":
        respond(req, {"supportsConfigurationDoneRequest": True, "supportsEvaluateForHovers": True})
        event("initialized")
    elif cmd == "launch":
        launch_request = req
        if FAIL_LAUNCH:
            respond(req, ok=False, message="cannot launch %s" % args.get("program", "?"))
            launch_request = None
    elif cmd == "setBreakpoints":
        path = args["source"]["path"]
        lines = [b["line"] for b in args.get("breakpoints", [])]
        breakpoints[path] = lines
        respond(req, {"breakpoints": [{"verified": True, "line": l} for l in lines]})
    elif cmd == "setExceptionBreakpoints":
        respond(req)
    elif cmd == "configurationDone":
        respond(req)
        if launch_request is not None:
            respond(launch_request, {})
            launch_request = None
        event("output", {"category": "stdout", "output": "hello from the program\n"})
        send({"type": "request", "command": "runInTerminal", "arguments": {"args": ["x"]}})
        first = next(((p, l[0]) for p, l in breakpoints.items() if l), None)
        if first:
            current["path"], current["line"] = first
            stop("breakpoint")
        else:
            event("exited", {"exitCode": 0})
            event("terminated")
    elif cmd == "threads":
        respond(req, {"threads": [{"id": 1, "name": "main"}]})
    elif cmd == "stackTrace":
        respond(
            req,
            {
                "stackFrames": [
                    {"id": 100, "name": "compute", "source": {"path": current["path"]}, "line": current["line"], "column": 5},
                    {"id": 101, "name": "main", "source": {"path": current["path"]}, "line": 1, "column": 1},
                    {"id": 102, "name": "[Native Frame]", "line": 0, "column": 0},
                ],
                "totalFrames": 3,
            },
        )
    elif cmd == "scopes":
        respond(
            req,
            {
                "scopes": [
                    {"name": "Locals", "variablesReference": 10, "expensive": False},
                    {"name": "Globals", "variablesReference": 11, "expensive": True},
                ]
            },
        )
    elif cmd == "variables":
        respond(req, {"variables": variables.get(args.get("variablesReference"), [])})
    elif cmd == "evaluate":
        if args.get("expression") == "boom":
            respond(req, ok=False, message="name 'boom' is not defined")
        else:
            respond(req, {"result": "<%s>" % args.get("expression"), "variablesReference": 0})
    elif cmd in ("next", "stepIn", "stepOut"):
        respond(req)
        current["line"] += 1
        stop("step")
    elif cmd == "pause":
        respond(req)
        stop("pause")
    elif cmd == "continue":
        respond(req, {"allThreadsContinued": True})
        event("continued", {"threadId": 1, "allThreadsContinued": True})
        event("exited", {"exitCode": 0})
        event("terminated")
    elif cmd == "disconnect":
        respond(req)
        break
    else:
        respond(req, ok=False, message="unsupported: %s" % cmd)
