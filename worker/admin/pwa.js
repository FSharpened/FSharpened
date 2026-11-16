// FSharpened Hub: installability, offline notice and update prompt. Loaded as a plain deferred script (strict CSP: no inline code).
// admin.js owns the console itself; the two only meet through the DOM and the browser's own online/offline events.
(() => {
  "use strict";
  const $ = (s) => document.querySelector(s);
  const bar = $("#net");
  const root = document.documentElement;
  let updateReady = false;

  /** One status strip, two messages. Being offline outranks "a new version is ready". */
  function renderBar() {
    if (!bar) return;
    bar.replaceChildren();
    if (!navigator.onLine) {
      bar.textContent = "You are offline. The console is open, but live data needs a connection.";
      bar.hidden = false;
    } else if (updateReady) {
      bar.append("A new version of the console is ready. ");
      const b = document.createElement("button");
      b.type = "button"; b.className = "btn ghost"; b.textContent = "Reload";
      b.addEventListener("click", () => location.reload());
      bar.append(b);
      bar.hidden = false;
    } else bar.hidden = true;
  }
  function netChanged() { root.classList.toggle("offline", !navigator.onLine); renderBar(); }
  addEventListener("online", netChanged);
  addEventListener("offline", netChanged);
  netChanged();

  // ---- service worker (needs https or localhost; silently skipped elsewhere and in browsers without it)
  if ("serviceWorker" in navigator) {
    const hadController = !!navigator.serviceWorker.controller;
    navigator.serviceWorker.addEventListener("controllerchange", () => { if (hadController) { updateReady = true; renderBar(); } });
    addEventListener("load", () => { navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(() => undefined); });
  }

  // ---- install button: native prompt where the browser offers one, a hint on iOS where it does not
  const btn = $("#install");
  if (!btn) return;
  const standalone = () => matchMedia("(display-mode: standalone)").matches || matchMedia("(display-mode: minimal-ui)").matches || navigator.standalone === true;
  const ios = /iphone|ipad|ipod/i.test(navigator.userAgent) || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
  let deferred = null;
  const say = (msg) => { const t = $("#toast"); if (!t) return; t.textContent = msg; t.classList.add("show"); setTimeout(() => t.classList.remove("show"), 5000); };

  addEventListener("beforeinstallprompt", (e) => { e.preventDefault(); deferred = e; if (!standalone()) btn.hidden = false; });
  addEventListener("appinstalled", () => { deferred = null; btn.hidden = true; say("Installed. Open it from your apps."); });
  if (ios && !standalone()) btn.hidden = false;
  btn.addEventListener("click", async () => {
    if (deferred) {
      const p = deferred; deferred = null; btn.hidden = true;
      try { await p.prompt(); await p.userChoice; } catch { /* dismissed */ }
    } else say("On iPhone and iPad: tap Share, then Add to Home Screen.");
  });
})();
