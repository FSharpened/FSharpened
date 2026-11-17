// FSharpened Hub admin UI. No framework, no innerHTML: all dynamic text goes through textContent (XSS-safe by construction).
const $ = (s, r = document) => r.querySelector(s);
const NS = "http://www.w3.org/2000/svg";

function el(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === false || v == null) continue;
    if (k === "class") e.className = v; else if (k.startsWith("on")) e.addEventListener(k.slice(2), v); else e.setAttribute(k, v === true ? "" : v);
  }
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
}
function svg(tag, attrs, ...kids) {
  const e = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs || {})) e.setAttribute(k, v);
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
}
const fmtS = (s) => (s >= 60 ? `${Math.floor(s / 60)}m ${Math.round(s % 60)}s` : `${(+s).toFixed(1)}s`);
const fmtT = (t) => (t ? new Date(t * 1000).toLocaleString() : "never");
const ago = (t) => { const d = Math.max(0, Date.now() / 1000 - t); return d < 90 ? "just now" : d < 5400 ? Math.round(d / 60) + " min ago" : d < 129600 ? Math.round(d / 3600) + " h ago" : Math.round(d / 86400) + " d ago"; };
const httpsOnly = (u) => { try { return new URL(u).protocol === "https:" ? u : null; } catch { return null; } };

/** A failed fetch (no network, DNS, captive portal) is reported as `offline` so views can say so plainly. */
async function api(path, opts = {}) {
  let res;
  try { res = await fetch("/api/admin" + path, { ...opts, headers: { "x-fsh-admin": "1", ...(opts.body ? { "content-type": "application/json" } : {}) } }); }
  catch { throw Object.assign(new Error("Cannot reach the hub. Check your connection."), { offline: true }); }
  if (!res.ok) { const j = await res.json().catch(() => ({})); throw new Error(j.error || res.statusText || "HTTP " + res.status); }
  return res.json();
}
let toastTimer;
function toast(msg) { const t = $("#toast"); t.textContent = msg; t.classList.add("show"); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove("show"), 2600); }
const badge = (ok) => el("span", {}, el("span", { class: "dot " + (ok ? "pass" : "fail") }), ok ? "pass" : "fail");

// ---------- charts
/** Drawn at the container's real pixel width, so labels stay 11px on a phone instead of shrinking with a fixed viewBox. */
function dailyChart(rows, W = 640) {
  W = Math.max(260, Math.round(W));
  const H = Math.round(Math.min(240, Math.max(170, W * 0.36))), pad = { l: 34, r: 40, t: 12, b: 26 }, every = W < 420 ? 3 : 2;
  const days = []; const today = Math.floor(Date.now() / 86400000);
  for (let i = 13; i >= 0; i--) { const d = today - i; const r = rows.find((x) => x.d === d); days.push({ d, pass: r?.pass || 0, fail: r?.fail || 0, avg: r?.avg_s || 0 }); }
  const max = Math.max(1, ...days.map((x) => x.pass + x.fail)), maxS = Math.max(1, ...days.map((x) => x.avg));
  const bw = (W - pad.l - pad.r) / days.length, y = (v) => pad.t + (H - pad.t - pad.b) * (1 - v / max);
  const s = svg("svg", { viewBox: `0 0 ${W} ${H}`, class: "chart", role: "img", "aria-label": "Runs per day, passes and failures, with average duration line" });
  for (let i = 0; i <= 4; i++) { const v = (max * i) / 4, yy = y(v); s.append(svg("line", { x1: pad.l, x2: W - pad.r, y1: yy, y2: yy, stroke: "currentColor", "stroke-opacity": ".12" }), svg("text", { x: pad.l - 6, y: yy + 4, "text-anchor": "end" }, Math.round(v))); }
  days.forEach((x, i) => {
    const bx = pad.l + i * bw + bw * 0.18, w = bw * 0.64;
    s.append(svg("rect", { x: bx, y: y(x.pass), width: w, height: y(0) - y(x.pass), fill: "var(--ok)", rx: 2 }, svg("title", {}, `${x.pass} passed`)));
    s.append(svg("rect", { x: bx, y: y(x.pass + x.fail), width: w, height: y(x.pass) - y(x.pass + x.fail), fill: "var(--bad)", rx: 2 }, svg("title", {}, `${x.fail} failed`)));
    if (i % every === 0) s.append(svg("text", { x: bx + w / 2, y: H - 8, "text-anchor": "middle" }, new Date(x.d * 86400000).toLocaleDateString(undefined, { month: "short", day: "numeric" })));
  });
  const pts = days.filter((x) => x.avg > 0).map((x) => { const i = days.indexOf(x); return `${pad.l + i * bw + bw / 2},${pad.t + (H - pad.t - pad.b) * (1 - x.avg / maxS)}`; });
  if (pts.length > 1) s.append(svg("polyline", { points: pts.join(" "), fill: "none", stroke: "var(--brand)", "stroke-width": 2 }), svg("text", { x: W - pad.r + 4, y: pad.t + 10 }, fmtS(maxS)));
  return s;
}
function chartBox(rows) { const b = el("div", { class: "chartbox" }); b.rows = rows; return b; }
function drawCharts(root) { root.querySelectorAll(".chartbox").forEach((b) => { const w = Math.round(b.clientWidth); if (w && w !== b.drawnW) { b.drawnW = w; b.replaceChildren(dailyChart(b.rows, w)); } }); }
function bars(items, labelFn, valFn, maxV, cls = () => "") {
  return el("div", {}, items.map((it) => el("div", { class: "bar" }, el("span", { title: labelFn(it) }, labelFn(it)),
    el("div", { class: "track" }, el("div", { class: "fill " + cls(it), style: null, "data-w": Math.max(2, (valFn(it) / maxV) * 100) })), el("span", { class: "muted" }, fmtS(valFn(it))))));
}
// CSP forbids inline styles; widths are applied through the CSSOM after insertion.
function applyWidths(root) { root.querySelectorAll("[data-w]").forEach((n) => { n.style.width = n.dataset.w + "%"; }); }

// ---------- views
const views = {
  async overview() {
    const o = await api("/overview");
    const pr = o.totals.passRate;
    const stats = el("div", { class: "grid g3" }, ...[
      ["Runs (30 d)", o.totals.runs30d], ["Pass rate", pr == null ? "–" : Math.round(pr * 100) + "%"],
      ["Median duration", o.totals.p50 ? fmtS(o.totals.p50) : "–"], ["p95 duration", o.totals.p95 ? fmtS(o.totals.p95) : "–"],
    ].map(([l, v]) => el("div", { class: "panel stat" }, el("div", { class: "v" }, v), el("div", { class: "l" }, l))));
    const matrix = el("div", { class: "grid g6" }, o.rids.map((r) => {
      const st = o.matrix[r];
      return el("div", { class: "panel rid " + (st === "none" ? "" : st) }, el("div", { class: "n" }, r), el("div", { class: "s" }, st === "none" ? "no data" : st));
    }));
    const recent = el("div", { class: "panel tw" }, el("h2", {}, "Recent runs"), runsTable(o.recent));
    const root = el("div", {}, el("h1", {}, "Overview"), el("p", { class: "sub" }, "Live view of the local-first CI pipeline across the six tier-1 RIDs."), stats,
      el("h2", { class: "pad" }, "Platform matrix"), matrix,
      el("div", { class: "grid g2 pad" }, el("div", { class: "panel" }, el("h2", {}, "Last 14 days"), chartBox(o.daily), el("p", { class: "muted" }, "Bars: passed (green) and failed (red). Line: average duration.")), recent));
    return root;
  },
  async runs() {
    const qs = new URLSearchParams(location.hash.split("?")[1] || "");
    const d = await api("/runs?" + qs);
    const rid = el("select", { "aria-label": "Filter by RID" }, el("option", { value: "" }, "All RIDs"), ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"].map((r) => el("option", { value: r, selected: qs.get("rid") === r }, r)));
    const st = el("select", { "aria-label": "Filter by result" }, ["", "pass", "fail"].map((s) => el("option", { value: s, selected: (qs.get("status") || "") === s }, s || "Any result")));
    const go = () => { const p = new URLSearchParams(); if (rid.value) p.set("rid", rid.value); if (st.value) p.set("status", st.value); location.hash = "#/runs" + (p.toString() ? "?" + p : ""); };
    rid.addEventListener("change", go); st.addEventListener("change", go);
    return el("div", {}, el("h1", {}, "Runs"), el("p", { class: "sub" }, "Every pipeline run published to the hub."), el("div", { class: "row" }, rid, st),
      el("div", { class: "panel tw" }, d.runs.length ? runsTable(d.runs) : el("p", { class: "muted" }, "No runs yet. See Settings → Tokens, then set FSH_HUB_URL and FSH_HUB_TOKEN.")));
  },
  async run(id) {
    const d = await api("/runs/" + encodeURIComponent(id));
    const r = d.run, max = Math.max(1, ...d.stages.map((s) => s.seconds)), link = httpsOnly(r.run_url);
    return el("div", {}, el("a", { href: "#/runs" }, "← Runs"), el("h1", {}, r.rid, " ", el("code", {}, r.profile)),
      el("p", { class: "sub" }, badge(!!r.ok), " · ", fmtS(r.duration_s), " · ", el("code", {}, r.commit_sha || "?"), r.branch ? ` on ${r.branch}` : "", " · ", fmtT(r.received_at), link ? [" · ", el("a", { href: link, rel: "noopener" }, "CI run")] : ""),
      el("div", { class: "panel" }, el("h2", {}, "Stage timeline"), bars(d.stages, (s) => s.name, (s) => s.seconds, max, (s) => (s.status === "fail" || s.status === "blocked" ? "fail" : s.status === "skip" ? "skip" : "")),
        d.stages.filter((s) => s.detail).map((s) => el("p", { class: "muted" }, el("code", {}, s.name), " ", s.detail))));
  },
  async stages() {
    const d = await api("/stages"), max = Math.max(1, ...d.stats.map((s) => s.p95));
    return el("div", {}, el("h1", {}, "Stage performance"), el("p", { class: "sub" }, "Median (bar) and p95 per stage over recent full-profile runs."),
      d.regressions.length ? el("div", { class: "notice" }, el("strong", {}, `${d.regressions.length} possible regression(s) `), `(latest run > ${d.thresholdPct}% slower than the median of the previous 10)`,
        el("ul", {}, d.regressions.map((g) => el("li", {}, el("code", {}, g.name), " on ", g.rid, `: ${fmtS(g.latest)} vs ${fmtS(g.baseline)} (+${g.pct}%)`)))) : el("div", { class: "notice ok" }, "No stage regressions detected."),
      el("div", { class: "panel" }, d.stats.length ? [bars(d.stats, (s) => s.name, (s) => s.p50, max), el("p", { class: "muted" }, "p95: ", d.stats.map((s) => `${s.name} ${fmtS(s.p95)}`).join(" · "))] : el("p", { class: "muted" }, "No data yet.")));
  },
  async settings() {
    const f = await api("/flags");
    const sw = (key, label, desc) => { const b = el("button", { class: "sw", role: "switch", "aria-checked": String(f[key]), "aria-label": label }); b.addEventListener("click", async () => { f[key] = !f[key]; b.setAttribute("aria-checked", String(f[key])); await save(); }); return el("div", { class: "field" }, el("div", {}, el("strong", {}, label), el("div", { class: "d" }, desc)), b); };
    const num = (key, label, desc, min, max) => { const i = el("input", { type: "number", min, max, value: f[key], "aria-label": label }); i.addEventListener("change", async () => { f[key] = Number(i.value); await save(); }); return el("div", { class: "field" }, el("div", {}, el("strong", {}, label), el("div", { class: "d" }, desc)), i); };
    async function save() { try { Object.assign(f, await api("/flags", { method: "PUT", body: JSON.stringify(f) })); toast("Saved"); } catch (e) { toast("Failed: " + e.message); } }
    return el("div", {}, el("h1", {}, "Settings"), el("p", { class: "sub" }, "Feature flags and thresholds. Changes apply immediately and are written to the audit log."),
      el("div", { class: "panel" }, sw("ingestEnabled", "Accept CI ingest", "Turn off to reject all incoming run reports (kill switch)."),
        sw("publicStatus", "Public status endpoint", "Exposes /api/status.json (matrix verdicts only) for the website."),
        num("regressionPct", "Regression threshold (%)", "A stage slower than the previous-10 median by this much is flagged.", 5, 300),
        num("retentionDays", "Retention (days)", "Runs older than this are deleted nightly.", 7, 730)));
  },
  async tokens() {
    const d = await api("/tokens"), out = el("div", {});
    const name = el("input", { type: "text", placeholder: "Token name (e.g. github-actions)", maxlength: 60, "aria-label": "Token name" });
    const create = el("button", { class: "btn" }, "Create token");
    create.addEventListener("click", async () => {
      if (!name.value.trim()) return toast("Name required");
      try { const t = await api("/tokens", { method: "POST", body: JSON.stringify({ name: name.value }) });
        out.replaceChildren(el("div", { class: "notice ok" }, el("strong", {}, "Copy this now: it is shown once."), el("code", { class: "secret" }, t.token), el("p", { class: "muted" }, "Store as the FSH_HUB_TOKEN secret and set FSH_HUB_URL to this hub's origin."))); toast("Created"); setTimeout(route, 20000);
      } catch (e) { toast("Failed: " + e.message); }
    });
    const rows = d.tokens.map((t) => { const rev = el("button", { class: "btn ghost", disabled: !!t.revoked_at }, t.revoked_at ? "Revoked" : "Revoke");
      rev.addEventListener("click", async () => { if (!confirm(`Revoke token "${t.name}"? Publishers using it will be rejected.`)) return; try { await api("/tokens/" + t.id, { method: "DELETE" }); toast("Revoked"); route(); } catch (e) { toast("Failed: " + e.message); } });
      return el("tr", {}, el("td", {}, t.name), el("td", {}, fmtT(t.created_at)), el("td", {}, t.created_by), el("td", {}, t.last_used ? ago(t.last_used) : "never"), el("td", {}, rev)); });
    return el("div", {}, el("h1", {}, "API tokens"), el("p", { class: "sub" }, "Bearer tokens for CI publishers. Only a SHA-256 hash is stored."),
      el("div", { class: "row" }, name, create), out, el("div", { class: "panel tw" }, rows.length ? el("table", {}, el("thead", {}, el("tr", {}, ["Name", "Created", "By", "Last used", ""].map((h) => el("th", {}, h)))), el("tbody", {}, rows)) : el("p", { class: "muted" }, "No tokens yet.")));
  },
  async audit() {
    const d = await api("/audit");
    return el("div", {}, el("h1", {}, "Audit log"), el("p", { class: "sub" }, "Every change made through this console."),
      el("div", { class: "panel tw" }, d.audit.length ? el("table", {}, el("thead", {}, el("tr", {}, ["When", "Who", "Action", "Detail"].map((h) => el("th", {}, h)))),
        el("tbody", {}, d.audit.map((a) => el("tr", {}, el("td", {}, fmtT(a.ts)), el("td", {}, a.actor), el("td", {}, el("code", {}, a.action)), el("td", {}, a.detail))))) : el("p", { class: "muted" }, "Nothing yet.")));
  },
};

function runsTable(rows) {
  return el("table", {}, el("thead", {}, el("tr", {}, ["Result", "RID", "Profile", "Duration", "Commit", "When"].map((h) => el("th", {}, h)))),
    el("tbody", {}, rows.map((r) => el("tr", {}, el("td", {}, badge(!!r.ok)), el("td", {}, el("a", { href: "#/runs/" + encodeURIComponent(r.id) }, r.rid)), el("td", {}, r.profile), el("td", {}, fmtS(r.duration_s)), el("td", {}, el("code", {}, r.commit_sha || "?")), el("td", {}, ago(r.received_at))))));
}

const NAV = [["#/", "Overview"], ["#/runs", "Runs"], ["#/stages", "Stage performance"], ["#/settings", "Settings"], ["#/tokens", "API tokens"], ["#/audit", "Audit log"]];
let showingError = false; // true while the view is an offline/error panel, so coming back online can retry it
let lastPath = null;      // focus moves to the new view only when the page changes, not on a filter change, refresh or retry

/** Scrollable tables must be reachable and named for keyboard and screen-reader users. */
function enhance(main) {
  main.querySelectorAll(".tw").forEach((n) => { n.tabIndex = 0; n.setAttribute("role", "region"); n.setAttribute("aria-label", n.querySelector("h2")?.textContent || "Data table"); });
}
function retryPanel(title, text) {
  const b = el("button", { class: "btn", type: "button" }, "Try again");
  b.addEventListener("click", () => route());
  return el("div", { class: "panel signin" }, el("h1", {}, title), el("p", { class: "sub" }, text), b);
}
async function route() {
  const h = location.hash.replace(/\?.*/, "") || "#/", main = $("#view");
  const label = (NAV.find(([href]) => (href === "#/" ? h === "#/" : h.startsWith(href))) || NAV[0])[1];
  $("#nav").replaceChildren(...NAV.map(([href, l]) => el("a", { href, "aria-current": l === label && (href === "#/" ? h === "#/" : h.startsWith(href)) ? "page" : false }, l)));
  $("#nav [aria-current]")?.scrollIntoView?.({ inline: "nearest", block: "nearest" }); // keep the current tab visible in the phone top bar
  document.title = label + " · FSharpened Hub";
  const moved = lastPath !== null && lastPath !== h; lastPath = h;
  try {
    const m = h.match(/^#\/runs\/(.+)$/);
    const v = m ? await views.run(decodeURIComponent(m[1])) : await (views[{ "#/": "overview", "#/runs": "runs", "#/stages": "stages", "#/settings": "settings", "#/tokens": "tokens", "#/audit": "audit" }[h] || "overview"])();
    showingError = false;
    main.replaceChildren(v); applyWidths(main); drawCharts(main); enhance(main);
  } catch (e) {
    if (e.message === "unauthorized") { showingError = false; return showSignIn(main); }
    showingError = true;
    main.replaceChildren(e.offline ? retryPanel("You are offline", "The console is open, but run data lives on the hub. It reloads by itself when your connection returns.") : retryPanel("Something went wrong", e.message));
  }
  if (moved) { scrollTo(0, 0); main.focus({ preventScroll: true }); }
}
function showSignIn(main) {
  $("#nav").replaceChildren();
  document.title = "Sign in · FSharpened Hub";
  main.replaceChildren(el("div", { class: "panel signin" }, el("img", { src: "/mark.svg", alt: "", width: 56, height: 56 }), el("h1", {}, "FSharpened Hub"),
    el("p", { class: "sub" }, "Sign in with an authorised GitHub account to manage CI telemetry, tokens and settings."),
    el("a", { class: "btn", href: "/api/auth/login" }, "Sign in with GitHub")));
}
function loadWho() {
  return api("/me").then((m) => {
    const who = $("#who"); who.textContent = m.email + (m.environment === "dev" ? " (dev) " : " ");
    if (m.environment !== "dev") { const b = el("button", { class: "btn ghost", type: "button" }, "Sign out"); b.addEventListener("click", async () => { await fetch("/api/auth/logout", { method: "POST", headers: { "x-fsh-admin": "1" } }); location.hash = "#/"; location.reload(); }); who.append(el("div", {}, b)); }
  }).catch(() => {});
}
addEventListener("hashchange", route);
addEventListener("online", () => { if (!$("#who").textContent) loadWho(); if (showingError) route(); });
if ("ResizeObserver" in window) new ResizeObserver(() => requestAnimationFrame(() => drawCharts($("#view")))).observe($("#view"));
else addEventListener("resize", () => drawCharts($("#view")));
loadWho();
route();
