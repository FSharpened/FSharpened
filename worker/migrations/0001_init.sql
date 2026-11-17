-- FSharpened hub schema. Telemetry only; no personal data is stored.
CREATE TABLE runs (
  id TEXT PRIMARY KEY,
  received_at INTEGER NOT NULL,
  started TEXT NOT NULL,
  profile TEXT NOT NULL,
  rid TEXT NOT NULL,
  ok INTEGER NOT NULL,
  duration_s REAL NOT NULL,
  commit_sha TEXT NOT NULL,
  branch TEXT NOT NULL,
  source TEXT NOT NULL,
  dirty INTEGER NOT NULL,
  sdk TEXT NOT NULL,
  run_url TEXT NOT NULL
);
CREATE INDEX runs_received ON runs(received_at DESC);
CREATE INDEX runs_rid ON runs(rid, received_at DESC);

CREATE TABLE stages (
  run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  status TEXT NOT NULL,
  seconds REAL NOT NULL,
  detail TEXT NOT NULL,
  PRIMARY KEY (run_id, name)
);
CREATE INDEX stages_name ON stages(name);

CREATE TABLE tokens (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  hash TEXT NOT NULL UNIQUE,
  created_at INTEGER NOT NULL,
  created_by TEXT NOT NULL,
  last_used INTEGER,
  revoked_at INTEGER
);

CREATE TABLE audit (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts INTEGER NOT NULL,
  actor TEXT NOT NULL,
  action TEXT NOT NULL,
  detail TEXT NOT NULL
);
CREATE INDEX audit_ts ON audit(ts DESC);
