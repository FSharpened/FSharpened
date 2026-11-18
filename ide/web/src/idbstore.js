// The IndexedDB side of the project store (ADR-0024). Only plain IndexedDB, taken as an argument (`indexedDB`), so the tests
// can hand it an in-memory fake and the page hands it the browser's.
//
//   database  fsharpened-ide, version 1
//   projects  one record per project, keyPath "id": the record the store keeps (project, tabs, input, choices, updated)
//   meta      "index"  { v: 3, current, order }   what the localStorage index held: which project is open, and their order
//             "info"   { migratedFrom, at }       written once, when the localStorage projects were copied here
//
// A project record and the index are written in ONE transaction, so a crash never leaves an index that names a project the
// database does not have, or a project the index forgets. Writes are queued one after the other.
export const DB_NAME = "fsharpened-ide";
const OPEN_TIMEOUT_MS = 4000;

const request = (r) => new Promise((ok, fail) => { r.onsuccess = () => ok(r.result); r.onerror = () => fail(r.error); });
const finished = (tx) => new Promise((ok, fail) => {
  tx.oncomplete = () => ok();
  tx.onerror = () => fail(tx.error);
  tx.onabort = () => fail(tx.error ?? new Error("the transaction was aborted"));
});

export const isQuota = (e) => /quota/i.test(String(e?.name) + " " + String(e?.message)) || e?.code === 22;

/** What a person is told when a write fails. Quota is the one failure they can do something about. */
export const explain = (e) => (isQuota(e)
  ? "The browser's storage is full. Download the project to keep it, or delete a project you no longer need."
  : "The browser would not save this project.");

/** Opens the database, or rejects when IndexedDB is missing, blocked (private windows) or does not answer in time. */
export function openDb(indexedDB, timeoutMs = OPEN_TIMEOUT_MS) {
  return new Promise((ok, fail) => {
    if (!indexedDB || typeof indexedDB.open !== "function") { fail(new Error("IndexedDB is not available")); return; }
    let settled = false;
    const done = (fn, v) => { if (settled) return; settled = true; clearTimeout(timer); fn(v); };
    const timer = setTimeout(() => done(fail, new Error("IndexedDB did not open in time")), timeoutMs);
    let req;
    try { req = indexedDB.open(DB_NAME, 1); } catch (e) { done(fail, e); return; }
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains("projects")) db.createObjectStore("projects", { keyPath: "id" });
      if (!db.objectStoreNames.contains("meta")) db.createObjectStore("meta");
    };
    req.onsuccess = () => {
      const db = req.result;
      db.onversionchange = () => db.close(); // another tab upgrades the database: step aside
      done(ok, db);
    };
    req.onerror = () => done(fail, req.error ?? new Error("IndexedDB would not open"));
    req.onblocked = () => done(fail, new Error("IndexedDB is blocked by another tab"));
  });
}

/**
 * The persister the store uses when IndexedDB works. Reads are async; writes return at once with { ok: true, done } where
 * `done` resolves to { ok, error? } when the transaction has really committed.
 */
export function createIdbPersister(db) {
  let tail = Promise.resolve();
  const queue = (job) => {
    const run = tail.then(job, job);
    tail = run.then(() => undefined, () => undefined);
    return run;
  };
  const guard = (job) => queue(job).then(() => ({ ok: true }), (e) => ({ ok: false, error: explain(e), cause: e }));
  const write = (job) => ({ ok: true, done: guard(job) });

  return {
    kind: "indexeddb",
    async readAll() {
      const tx = db.transaction(["projects", "meta"], "readonly");
      const done = finished(tx);
      const [records, index, info] = await Promise.all([request(tx.objectStore("projects").getAll()), request(tx.objectStore("meta").get("index")), request(tx.objectStore("meta").get("info"))]);
      await done;
      return { index: index ?? null, info: info ?? null, records: new Map(records.map((r) => [r.id, r])) };
    },
    /** One project and the index, in one transaction. */
    putRecord(record, index) {
      return write(async () => {
        const tx = db.transaction(["projects", "meta"], "readwrite");
        const done = finished(tx);
        tx.objectStore("projects").put(record);
        tx.objectStore("meta").put(index, "index");
        await done;
      });
    },
    putIndex(index) {
      return write(async () => {
        const tx = db.transaction(["meta"], "readwrite");
        const done = finished(tx);
        tx.objectStore("meta").put(index, "index");
        await done;
      });
    },
    removeRecord(id, index) {
      return write(async () => {
        const tx = db.transaction(["projects", "meta"], "readwrite");
        const done = finished(tx);
        tx.objectStore("projects").delete(id);
        tx.objectStore("meta").put(index, "index");
        await done;
      });
    },
    /** Several records, the index and the migration note, all or nothing. */
    putAll(records, index, info) {
      return write(async () => {
        const tx = db.transaction(["projects", "meta"], "readwrite");
        const done = finished(tx);
        for (const r of records) tx.objectStore("projects").put(r);
        tx.objectStore("meta").put(index, "index");
        if (info) tx.objectStore("meta").put(info, "info");
        await done;
      });
    },
    /** Empties the database (only used to undo a migration that could not be verified). */
    clear() {
      return guard(async () => {
        const tx = db.transaction(["projects", "meta"], "readwrite");
        const done = finished(tx);
        tx.objectStore("projects").clear();
        tx.objectStore("meta").clear();
        await done;
      });
    },
    /** Resolves when everything queued so far has been committed or has failed. */
    settled: () => tail,
    close: () => { try { db.close(); } catch { /* already closed */ } },
  };
}
