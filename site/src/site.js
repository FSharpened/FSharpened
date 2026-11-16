/* FSharpened site behaviour. Everything here is an enhancement: with JavaScript off the pages still read and
   navigate. Built into assets/h/site.<hash>.js by tools/site/build.py. */
(function () {
  "use strict";
  var d = document;
  function $(s, c) { return (c || d).querySelector(s); }
  function $$(s, c) { return Array.prototype.slice.call((c || d).querySelectorAll(s)); }

  // ---- pillar filters --------------------------------------------------------------------------------------
  var fb = $$(".filters button"), cards = $$("[data-layer]");
  fb.forEach(function (x) {
    x.addEventListener("click", function () {
      fb.forEach(function (y) { y.setAttribute("aria-pressed", y === x ? "true" : "false"); });
      var f = x.dataset.f;
      cards.forEach(function (e) { e.hidden = !(f === "all" || e.dataset.layer === f || e.dataset.stab === f); });
    });
  });

  // ---- mobile menu -----------------------------------------------------------------------------------------
  var menu = $("#menu"), nav = $("#nav");
  if (menu && nav) {
    var setOpen = function (open) {
      menu.setAttribute("aria-expanded", String(open));
      menu.setAttribute("aria-label", open ? "Close menu" : "Open menu");
      nav.classList.toggle("open", open);
    };
    menu.addEventListener("click", function () { setOpen(menu.getAttribute("aria-expanded") !== "true"); });
    d.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && nav.classList.contains("open")) { setOpen(false); menu.focus(); }
    });
    d.addEventListener("click", function (e) {
      if (nav.classList.contains("open") && !nav.contains(e.target) && !menu.contains(e.target)) setOpen(false);
    });
    nav.addEventListener("click", function (e) { if (e.target.closest && e.target.closest("a")) setOpen(false); });
    if (window.matchMedia) {
      var wide = window.matchMedia("(min-width: 62.5em)");
      var onWide = function () { if (wide.matches) setOpen(false); };
      if (wide.addEventListener) wide.addEventListener("change", onWide); else if (wide.addListener) wide.addListener(onWide);
    }
  }

  // ---- theme button (auto -> light -> dark) ----------------------------------------------------------------
  var tb = $("#theme");
  if (tb && window.fshTheme) {
    var NEXT = { auto: "light", light: "dark", dark: "auto" };
    var NAME = { auto: "automatic (follows your device)", light: "light", dark: "dark" };
    var paint = function () {
      var m = window.fshTheme.get();
      tb.setAttribute("data-mode", m);
      tb.setAttribute("aria-label", "Theme: " + NAME[m] + ". Switch to " + NAME[NEXT[m]]);
      tb.title = "Theme: " + NAME[m];
    };
    tb.addEventListener("click", function () { window.fshTheme.set(NEXT[window.fshTheme.get()]); paint(); });
    paint();
  }

  // ---- install as an app -----------------------------------------------------------------------------------
  var inst = $("#install"), deferred = null;
  window.addEventListener("beforeinstallprompt", function (e) {
    e.preventDefault();
    deferred = e;
    if (inst) inst.hidden = false;
  });
  if (inst) {
    inst.addEventListener("click", function () {
      if (!deferred) return;
      deferred.prompt();
      deferred.userChoice.then(function () { deferred = null; inst.hidden = true; });
    });
  }
  window.addEventListener("appinstalled", function () { deferred = null; if (inst) inst.hidden = true; });

  // ---- service worker and update notice --------------------------------------------------------------------
  function notifyUpdate() {
    if ($("#pwa-toast")) return;
    var t = d.createElement("div");
    t.id = "pwa-toast";
    t.setAttribute("role", "status");
    var msg = d.createElement("span");
    msg.textContent = "A new version is ready.";
    var go = d.createElement("button");
    go.type = "button";
    go.textContent = "Reload";
    go.addEventListener("click", function () { location.reload(); });
    var later = d.createElement("button");
    later.type = "button";
    later.className = "quiet";
    later.textContent = "Later";
    later.addEventListener("click", function () { t.remove(); });
    t.appendChild(msg); t.appendChild(go); t.appendChild(later);
    d.body.appendChild(t);
  }
  var secure = location.protocol === "https:" || location.hostname === "localhost" || location.hostname === "127.0.0.1";
  if ("serviceWorker" in navigator && secure) {
    var hadController = !!navigator.serviceWorker.controller;
    navigator.serviceWorker.addEventListener("controllerchange", function () {
      if (hadController) notifyUpdate();
      hadController = true;
    });
    window.addEventListener("load", function () {
      navigator.serviceWorker.register("/sw.js", { scope: "/" }).catch(function () { /* offline support is optional */ });
    });
  }

  // ---- offline page ----------------------------------------------------------------------------------------
  $$("[data-retry]").forEach(function (b) { b.addEventListener("click", function () { location.reload(); }); });
  if ($(".offline-page")) window.addEventListener("online", function () { location.reload(); });

  // ---- IDE load watchdog -----------------------------------------------------------------------------------
  var ide = $("#ide");
  if (ide) {
    setTimeout(function () {
      if (window.__ideReady) return;
      var s = $("#status");
      if (s) s.textContent = "The IDE could not start. Check your connection and reload the page.";
    }, 15000);
  }
})();
