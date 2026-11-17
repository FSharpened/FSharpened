// Small DOM helpers shared by the IDE shell. No innerHTML anywhere: text always goes in as text nodes.
export const $ = (s, root = document) => root.querySelector(s);

export const el = (tag, attrs = {}, ...kids) => {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k === "class") e.className = v;
    else e.setAttribute(k, v === true ? "" : v);
  }
  for (const c of kids.flat()) if (c != null) e.append(c.nodeType ? c : document.createTextNode(String(c)));
  return e;
};

export const safe = (fn, fallback) => {
  try {
    return fn();
  } catch {
    return fallback;
  }
};

/** `replaceChildren` that skips null and false (it would otherwise write the text "null" into the page). */
export const fill = (node, ...kids) => {
  node.replaceChildren(...kids.flat().filter((c) => c != null && c !== false));
  return node;
};
