// The MKWMP website: theme, launcher-style focus (arrows / gamepad), the live server list, the
// latest release, copy buttons and the privacy policy viewer. No framework, no tracking.
(() => {
  "use strict";
  const T = window.I18N || {};
  const root = document.documentElement;
  const base = root.dataset.root || "";
  const BEHOLDER = "https://beholder.anarqz.dev/beholder/v1/servers";
  const OFFICIAL = "https://mkw.anarqz.dev";
  const REPO = "anarqz/mkwmp";
  const $ = (s, el = document) => el.querySelector(s);
  const $$ = (s, el = document) => [...el.querySelectorAll(s)];
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const fmt = (s, o) => String(s || "").replace(/\{(\w+)\}/g, (_, k) => (k in o ? o[k] : ""));

  // ── theme: the visitor's choice, else the system's; screenshots follow it ──
  const forced = new URLSearchParams(location.search).get("theme");
  const stored = /^(light|dark)$/.test(forced || "") ? forced : (() => { try { return localStorage.getItem("mkwmp-theme"); } catch { return null; } })();
  const media = matchMedia("(prefers-color-scheme: dark)");
  const setTheme = (t, save) => {
    root.dataset.theme = t;
    if (save) try { localStorage.setItem("mkwmp-theme", t); } catch {}
    $$("img[data-shot]").forEach((img) => { img.src = `${base}assets/launcher/${img.dataset.shot}-${t}.webp`; });
    const b = $("#theme"); if (b) b.setAttribute("aria-label", t === "dark" ? T.themeLight : T.themeDark);
  };
  setTheme(stored || (media.matches ? "dark" : "light"), false);
  media.addEventListener?.("change", (e) => { if (!stored) setTheme(e.matches ? "dark" : "light", false); });
  const toggleTheme = () => setTheme(root.dataset.theme === "dark" ? "light" : "dark", true);
  $("#theme")?.addEventListener("click", toggleTheme);

  // ── toast ──
  let toastTimer;
  const toast = (msg) => {
    const t = $("#toast"); if (!t) return;
    $("span", t).textContent = msg; t.classList.add("is-on");
    clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove("is-on"), 1800);
  };

  // ── copy a command ──
  $$(".cmd").forEach((b) => b.addEventListener("click", async () => {
    const text = $("code", b).textContent;
    try { await navigator.clipboard.writeText(text); toast(T.copied); } catch { toast(T.copyFailed); }
  }));

  // ── launcher-style focus: arrows (or the d-pad) move to the nearest focusable control ──
  const focusables = () => $$(".focusable, .page a.row, .page summary.row").filter((el) => {
    const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && !el.closest("[hidden], .modal:not(.is-open)");
  }).filter((el) => !$(".modal.is-open") || el.closest(".modal.is-open"));
  const move = (dir) => {
    const list = focusables(); if (!list.length) return;
    const cur = document.activeElement;
    if (!list.includes(cur)) { const first = list.find((el) => el.getBoundingClientRect().top >= 0) || list[0]; first.focus({ preventScroll: false }); return; }
    const a = cur.getBoundingClientRect(), ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    let best = null, bestScore = Infinity;
    for (const el of list) {
      if (el === cur) continue;
      const b = el.getBoundingClientRect(), bx = b.left + b.width / 2, by = b.top + b.height / 2;
      const dx = bx - ax, dy = by - ay;
      const along = { up: -dy, down: dy, left: -dx, right: dx }[dir];
      if (along <= 4) continue;
      const across = dir === "up" || dir === "down" ? Math.abs(dx) : Math.abs(dy);
      const score = along + across * 2.2;
      if (score < bestScore) { bestScore = score; best = el; }
    }
    if (best) { best.focus({ preventScroll: true }); best.scrollIntoView({ block: "nearest", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" }); }
  };
  const back = () => {
    const open = $(".modal.is-open"); if (open) { closeModal(open); return; }
    scrollTo({ top: 0 }); $(".brand")?.focus({ preventScroll: true });
  };
  addEventListener("keydown", (e) => {
    if (e.target.closest("input, textarea, select") || e.altKey || e.ctrlKey || e.metaKey) return;
    const dir = { ArrowUp: "up", ArrowDown: "down", ArrowLeft: "left", ArrowRight: "right" }[e.key];
    if (dir) { e.preventDefault(); document.body.classList.remove("uses-pad"); move(dir); }
    else if (e.key === "Escape") { e.preventDefault(); back(); }
    else if ((e.key === "t" || e.key === "T") && !e.shiftKey) { toggleTheme(); }
  });
  $$("[data-hint=back]").forEach((h) => h.addEventListener("click", back));
  $$("[data-hint=theme]").forEach((h) => h.addEventListener("click", toggleTheme));
  $$("[data-hint=move]").forEach((h) => h.addEventListener("click", () => move("down")));

  // ── gamepad: d-pad / left stick move, A = OK, B = back, Y = theme (repeat 380 ms, then 105 ms) ──
  const held = {}; let padRaf = 0;
  const press = (key, now, fn) => {
    const h = held[key];
    if (!h) { held[key] = { t: now, n: 0 }; fn(); return; }
    if (now - h.t > (h.n ? 105 : 380)) { h.t = now; h.n++; fn(); }
  };
  const pollPad = () => {
    const pads = navigator.getGamepads ? [...navigator.getGamepads()].filter(Boolean) : [];
    $(".hintbar__device")?.classList.toggle("has-pad", pads.length > 0);
    const now = performance.now(), seen = {};
    for (const p of pads) {
      const b = (i) => p.buttons[i]?.pressed, ax = p.axes[0] || 0, ay = p.axes[1] || 0;
      const act = { up: b(12) || ay < -0.6, down: b(13) || ay > 0.6, left: b(14) || ax < -0.6, right: b(15) || ax > 0.6, a: b(0), b: b(1), y: b(3) };
      for (const [k, on] of Object.entries(act)) if (on) {
        seen[k] = true; document.body.classList.add("uses-pad");
        if (k in { up: 1, down: 1, left: 1, right: 1 }) press(k, now, () => move(k));
        else if (!held[k]) { held[k] = { t: now, n: 0 }; if (k === "a") document.activeElement?.click(); else if (k === "b") back(); else toggleTheme(); }
      }
    }
    for (const k of Object.keys(held)) if (!seen[k]) delete held[k];
    padRaf = pads.length ? requestAnimationFrame(pollPad) : 0;
  };
  addEventListener("gamepadconnected", () => { if (!padRaf) padRaf = requestAnimationFrame(pollPad); $(".hintbar__device")?.classList.add("has-pad"); });
  addEventListener("gamepaddisconnected", () => { if (!navigator.getGamepads || ![...navigator.getGamepads()].some(Boolean)) $(".hintbar__device")?.classList.remove("has-pad"); });

  // ── the nav follows the section in view ──
  const navLinks = $$(".nav a[href^='#']");
  if (navLinks.length && "IntersectionObserver" in window) {
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) if (e.isIntersecting) navLinks.forEach((a) => a.classList.toggle("is-current", a.getAttribute("href") === "#" + e.target.id));
    }, { rootMargin: "-40% 0px -55% 0px" });
    navLinks.forEach((a) => { const s = document.getElementById(a.getAttribute("href").slice(1)); if (s) io.observe(s); });
  }

  // ── the latest release: version and the launcher zip ──
  (async () => {
    const outs = $$("[data-version]"), links = $$("[data-download]");
    if (!outs.length && !links.length) return;
    try {
      const r = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: "application/vnd.github+json" } });
      if (!r.ok) return;
      const rel = await r.json();
      outs.forEach((o) => { o.textContent = rel.tag_name; });
      const zip = (rel.assets || []).find((a) => /^mkwmp-[\d.]+-windows-x64\.zip$/.test(a.name));
      const srv = (rel.assets || []).find((a) => /^mkwmp-server-[\d.]+\.zip$/.test(a.name));
      links.forEach((l) => { const a = l.dataset.download === "server" ? srv : zip; if (a) l.href = a.browser_download_url; });
    } catch {}
  })();

  // ── the live server list (the directory, like the launcher's "Choose a Server") ──
  const list = $("#server-list");
  const timeIt = async (endpoint) => {
    if (!/^https:/.test(endpoint)) return null;        // an http:// server can't be timed from an https page
    let best = null;
    for (let i = 0; i < 2; i++) {
      const t0 = performance.now();
      try {
        const c = new AbortController(); const to = setTimeout(() => c.abort(), 4000);
        const r = await fetch(endpoint + "/seat/v1/status", { cache: "no-store", signal: c.signal }); clearTimeout(to);
        if (!r.ok) return null;
        const ms = performance.now() - t0; best = best === null ? ms : Math.min(best, ms);
      } catch { return null; }
    }
    return Math.round(best);
  };
  const quality = (ms) => (ms === null ? 0 : ms < 80 ? 3 : ms < 160 ? 2 : 1);
  const renderServers = async () => {
    if (!list) return;
    list.innerHTML = `<div class="servers__empty"><span class="spinner"></span> ${esc(T.loading)}</div>`;
    let items;
    const art = root.dataset.emptyArt ? `<img src="${root.dataset.emptyArt}" alt="">` : "";
    try { const r = await fetch(BEHOLDER, { cache: "no-store" }); items = (await r.json()).items || []; }
    catch { list.innerHTML = `<div class="servers__empty">${art}${esc(T.serversError)}</div>`; return; }
    if (!items.length) { list.innerHTML = `<div class="servers__empty">${art}${esc(T.serversNone)}</div>`; return; }
    const rows = items.slice(0, 12).map((s) => ({ s, ms: undefined }));
    const draw = () => {
      rows.sort((a, b) => (a.ms ?? 1e9) - (b.ms ?? 1e9) || b.s.seated - a.s.seated);
      list.innerHTML = rows.map(({ s, ms }) => {
        const host = s.endpoint.replace(/^https?:\/\//, "");
        const lat = ms === undefined ? `<span class="spinner"></span>` : ms === null ? "—" : `${ms} ms`;
        const seats = s.queued > 0 ? fmt(T.inQueue, { n: s.queued }) : T.playing;
        const meta = [s.region, "v" + s.version.replace(/^v/, ""), host].filter(Boolean).map(esc).join(" · ");
        return `<div class="srv focusable" tabindex="0" title="${esc(s.endpoint)}">
          <span class="srv__ms"><span class="bars" data-q="${quality(ms ?? null)}"><i></i><i></i><i></i></span>${lat}</span>
          <span><span class="srv__name">${esc(s.name)}</span><span class="srv__meta">${meta}</span></span>
          <span class="srv__seats">${s.seated}/${s.capacity}<small>${esc(seats)}</small></span></div>`;
      }).join("");
    };
    draw();
    await Promise.all(rows.map(async (row) => { row.ms = await timeIt(row.s.endpoint); }));
    draw();
    const c = $("#servers-count"); if (c) c.textContent = fmt(items.length === 1 ? T.serversCount1 : T.serversCount, { n: items.length, p: items.reduce((n, s) => n + s.seated, 0) });
  };
  if (list) { renderServers(); $("#servers-refresh")?.addEventListener("click", renderServers); }

  // ── the official server's privacy policy, from the server itself (GET /privacy, Markdown) ──
  const md = (src) => {
    const inline = (s) => esc(s)
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/\[([^\]]+)\]\((https?:\/\/[^)\s]+)\)/g, '<a href="$2" rel="noopener">$1</a>')
      .replace(/(^|[\s(])(https?:\/\/[^\s)<]+)/g, '$1<a href="$2" rel="noopener">$2</a>');
    const out = []; let para = [], listOpen = false;
    const flush = () => { if (para.length) { out.push(`<p>${inline(para.join(" "))}</p>`); para = []; } if (listOpen) { out.push("</ul>"); listOpen = false; } };
    for (const line of src.split(/\r?\n/)) {
      const h = line.match(/^(#{1,3})\s+(.*)$/), li = line.match(/^\s*[-*]\s+(.*)$/);
      if (h) { flush(); out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`); }
      else if (li) { if (para.length) { out.push(`<p>${inline(para.join(" "))}</p>`); para = []; } if (!listOpen) { out.push("<ul>"); listOpen = true; } out.push(`<li>${inline(li[1])}</li>`); }
      else if (!line.trim()) flush();
      else { if (listOpen) { out.push("</ul>"); listOpen = false; } para.push(line.trim()); }
    }
    flush(); return out.join("\n");
  };
  let lastFocus = null;
  const openModal = (m) => { lastFocus = document.activeElement; m.classList.add("is-open"); m.setAttribute("aria-hidden", "false"); $(".modal__foot", m)?.focus(); };
  const closeModal = (m) => { m.classList.remove("is-open"); m.setAttribute("aria-hidden", "true"); lastFocus?.focus?.(); };
  const policy = $("#policy");
  if (policy) {
    $(".modal__foot", policy).addEventListener("click", () => closeModal(policy));
    policy.addEventListener("click", (e) => { if (e.target === policy) closeModal(policy); });
    $$("[data-policy]").forEach((b) => b.addEventListener("click", async () => {
      const body = $(".modal__body", policy);
      body.innerHTML = `<p><span class="spinner"></span> ${esc(T.loading)}</p>`;
      openModal(policy);
      try {
        const r = await fetch(OFFICIAL + "/privacy", { cache: "no-store" });
        if (!r.ok) throw 0;
        const v = (r.headers.get("ETag") || "").replace(/"/g, "");
        body.innerHTML = md(await r.text()) + (v ? `<p class="rows-note">${esc(fmt(T.policyVersion, { v: v.slice(0, 12) }))}</p>` : "");
      } catch { body.innerHTML = `<p>${esc(T.policyError)}</p><p><a href="${OFFICIAL}/privacy">${OFFICIAL}/privacy</a></p>`; }
    }));
  }
})();
