// A small in-memory IndexedDB for the tests: just the part idbstore.js uses (open with an upgrade, object stores with a key
// path or out-of-line keys, get / getAll / put / delete / clear, transactions that commit or abort as a whole), and the ways a
// real browser fails: a full disk (QuotaExceededError), a database that will not open, one that never answers, and a page that
// is closed before a transaction commits. No runtime dependency; it only exists for `npm test`.

const later = (fn) => setTimeout(fn, 0);

class Store {
  constructor(keyPath) { this.keyPath = keyPath; this.data = new Map(); }
  keyOf(value, key) { return this.keyPath ? value[this.keyPath] : key; }
}

class Request {
  constructor() { this.result = undefined; this.error = null; this.onsuccess = null; this.onerror = null; }
}

class Transaction {
  constructor(db, names, mode) {
    this.db = db; this.mode = mode; this.names = names; this.error = null;
    this.oncomplete = null; this.onerror = null; this.onabort = null;
    this.pending = 0; this.finished = false;
    // writes go to a staged copy and replace the real data only when the transaction completes: all or nothing
    this.staged = new Map(names.map((n) => [n, new Map(db.stores.get(n).data)]));
    later(() => this.settle());
  }

  objectStore(name) {
    if (!this.names.includes(name)) throw new DOMException("not part of this transaction", "NotFoundError");
    const store = this.db.stores.get(name), tx = this, staged = this.staged.get(name);
    const run = (work, writes) => {
      if (this.finished) throw new DOMException("the transaction has finished", "TransactionInactiveError");
      if (writes && this.mode !== "readwrite") throw new DOMException("read-only transaction", "ReadOnlyError");
      const req = new Request();
      this.pending++;
      later(() => {
        if (tx.finished) return;
        try {
          if (writes && tx.db.fake.failWrites) throw new DOMException("quota", tx.db.fake.failWrites);
          req.result = work();
          req.onsuccess?.();
        } catch (e) {
          req.error = e;
          tx.error = e;
          req.onerror?.();
          tx.onerror?.();
          tx.fail(e);
          return;
        }
        tx.pending--;
        later(() => tx.settle());
      });
      return req;
    };
    return {
      get: (key) => run(() => structuredClone(staged.get(key)), false),
      getAll: () => run(() => { const all = [...staged.values()].map((v) => structuredClone(v)); return tx.db.fake.tamper ? tx.db.fake.tamper(all) : all; }, false),
      put: (value, key) => run(() => { const k = store.keyOf(value, key); if (k === undefined) throw new DOMException("no key", "DataError"); staged.set(k, structuredClone(value)); return k; }, true),
      delete: (key) => run(() => { staged.delete(key); }, true),
      clear: () => run(() => { staged.clear(); }, true),
    };
  }

  fail(e) {
    if (this.finished) return;
    this.finished = true;
    this.error = e;
    later(() => this.onabort?.());
  }

  settle() {
    if (this.finished || this.pending > 0) return;
    this.finished = true;
    if (this.db.fake.dropCommits) return; // the page was closed before this transaction committed
    for (const [n, m] of this.staged) this.db.stores.get(n).data = m;
    this.db.fake.commits++;
    this.oncomplete?.();
  }
}

class Database {
  constructor(fake, name) {
    this.fake = fake; this.name = name; this.version = 0; this.stores = new Map();
    this.objectStoreNames = { contains: (n) => this.stores.has(n) };
    this.onversionchange = null; this.closed = false;
  }
  createObjectStore(name, options = {}) { const s = new Store(options.keyPath); this.stores.set(name, s); return s; }
  transaction(names, mode = "readonly") {
    if (this.closed) throw new DOMException("closed", "InvalidStateError");
    return new Transaction(this, Array.isArray(names) ? names : [names], mode);
  }
  close() { this.closed = true; }
}

export class FakeIndexedDB {
  constructor() {
    this.dbs = new Map();
    this.mode = "ok";            // "ok" | "throws" | "error" | "blocked" | "hang"
    this.failWrites = null;      // e.g. "QuotaExceededError": every write request fails and the transaction aborts
    this.dropCommits = false;    // true: transactions never commit (a page closed mid-write)
    this.tamper = null;          // (rows) => rows: what a reader sees, to simulate a copy that came back different
    this.commits = 0;
  }

  open(name, version = 1) {
    if (this.mode === "throws") throw new DOMException("denied", "SecurityError");
    const req = new Request();
    req.onupgradeneeded = null; req.onblocked = null;
    if (this.mode === "hang") return req;
    later(() => {
      if (this.mode === "error") { req.error = new DOMException("the database could not be opened", "UnknownError"); req.onerror?.(); return; }
      if (this.mode === "blocked") { req.onblocked?.(); return; }
      let db = this.dbs.get(name);
      const fresh = !db;
      if (fresh) { db = new Database(this, name); this.dbs.set(name, db); }
      db.closed = false;
      req.result = db;
      if (fresh || db.version < version) { db.version = version; req.onupgradeneeded?.(); }
      req.onsuccess?.();
    });
    return req;
  }

  /** Test helper: what is stored, without going through the API under test. */
  peek(name = "fsharpened-ide") {
    const db = this.dbs.get(name);
    if (!db) return null;
    return Object.fromEntries([...db.stores].map(([n, s]) => [n, Object.fromEntries(s.data)]));
  }
}
