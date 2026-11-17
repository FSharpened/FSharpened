/* Runs in <head>, before first paint: marks JavaScript as available and applies the saved theme so there is no
   flash of the wrong colours. Exposes window.fshTheme for the header button (site.js). */
(function () {
  "use strict";
  var root = document.documentElement, KEY = "fsh-theme";
  root.classList.add("js");

  function saved() {
    try {
      var v = localStorage.getItem(KEY);
      return v === "light" || v === "dark" ? v : "auto";
    } catch (e) {
      return "auto";
    }
  }

  function apply(mode) {
    if (mode === "auto") root.removeAttribute("data-theme");
    else root.setAttribute("data-theme", mode);
    // Keep the browser chrome (mobile address bar, installed-app title bar) in step with the page.
    var metas = document.querySelectorAll('meta[name="theme-color"]');
    for (var i = 0; i < metas.length; i++) {
      var m = metas[i];
      if (mode === "auto") {
        m.setAttribute("content", m.getAttribute("data-c"));
        m.setAttribute("media", m.getAttribute("data-m"));
      } else {
        m.setAttribute("content", mode === "dark" ? "#0b1020" : "#ffffff");
        m.removeAttribute("media");
      }
    }
  }

  window.fshTheme = {
    get: saved,
    set: function (mode) {
      try {
        if (mode === "auto") localStorage.removeItem(KEY);
        else localStorage.setItem(KEY, mode);
      } catch (e) {}
      apply(mode);
    },
  };

  if (saved() !== "auto") apply(saved());
  window.addEventListener("storage", function (e) {
    if (e.key === KEY) apply(saved());
  });
})();
