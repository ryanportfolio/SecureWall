// Tour section: the SecureWall product walkthrough, played as one continuous
// film between the download cards and "Before you install" in #team.
//
// The walkthrough player (tools/site/walkthrough, served at /walkthrough/)
// runs in a same-origin iframe with ?capture=1&fill=1: stage only, filling
// the frame. This script owns the clock, as tools/site/walkthrough/film.html
// does: it lays the chapters end to end and draws each frame with
// __player.set({ scene, t }). Next to the picture, the chapters are listed as
// a session log; each row logs its result (ALLOWED, IGNORED, ...) once its
// chapter has played, and clicking a row jumps to it.
//
// Playback: starts when the section is in view, pauses when it leaves, loops
// after a short hold on the last frame. Pause, the scrubber (a native range
// input) and the rows are the controls. With prefers-reduced-motion nothing
// plays until Play is pressed; while paused, a chapter picked from the log
// shows its end frame, and Play then plays that chapter from its start. The app theme switch (Dark / Light) sets the replayed Windows app's
// theme; the site itself has one theme.
//
// The iframe is created when the section comes near the viewport. If the
// player does not load, the section shows a link to the tour page instead.
// Keyboard and screen-reader users get real controls: the log rows and the
// bar are buttons and a range input. The picture is inert in the player and
// skipped by Tab here; the log carries each chapter's title and takeaway.
(() => {
  "use strict";

  const SRC = "/walkthrough/index.html?capture=1&fill=1&dev=0";
  // Result logged for each chapter once it has played; kind sets its colour
  // (allow: cyan, block: violet, as the Control section's cards).
  const RESULTS = {
    "ch1-allow": ["ALLOWED", "allow"],
    "ch2-ignore": ["IGNORED", "block"],
    "ch3-always-allow": ["ALWAYS ALLOW", "allow"],
    "ch4-activity": ["UNBLOCKED", "allow"],
    "ch5-rules": ["FILTERED", "info"],
    "ch6-modes": ["BLOCK ALL", "block"],
    "ch7-lock": ["LOCKED", "block"],
    "ch8-update": ["UPDATING", "info"],
  };
  // Hold on the last frame before the film starts again (ms).
  const HOLD = 1400;
  // Shown before anything plays with reduced motion: the first popup, open.
  const POSTER = { scene: "ch1-allow", at: 0.55 };
  const THEMES = [["dark", "Dark"], ["light", "Light"]];

  const reduceQuery = matchMedia("(prefers-reduced-motion: reduce)");
  const fmt = ms => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
  };
  const el = (tag, cls, attrs = {}) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
    return e;
  };

  function build() {
    const box = el("div", "sw-tour", { "data-sw-section": "tour", role: "region", "aria-labelledby": "sw-tour-title" });
    box.innerHTML = `
      <div class="sw-tour__head">
        <span class="sw-tour__chip">Session replay</span>
        <h3 class="sw-tour__title" id="sw-tour-title">What you will see after installing</h3>
        <p class="sw-tour__lede">SecureWall's windows, rebuilt for this page and replayed on a loop. The apps in it are made up.</p>
      </div>
      <div class="sw-tour__body">
        <div class="sw-tour__screen">
          <div class="sw-tour__frame">
            <button class="sw-tour__start" type="button" hidden><span class="sw-tour__start-icon" aria-hidden="true"></span>Play the tour</button>
            <a class="sw-tour__fallback" href="/walkthrough/" hidden>Open the tour</a>
          </div>
          <div class="sw-tour__bar">
            <button class="sw-tour__play" type="button" aria-label="Pause the tour"><span aria-hidden="true"></span></button>
            <div class="sw-tour__scrub">
              <div class="sw-tour__ticks" aria-hidden="true"></div>
              <input class="sw-tour__range" type="range" min="0" max="1" step="1" value="0" aria-label="Tour position">
            </div>
            <span class="sw-tour__time" aria-hidden="true">0:00 / 0:00</span>
            <div class="sw-tour__theme" role="group" aria-label="App theme"></div>
          </div>
        </div>
        <ol class="sw-tour__log" aria-label="Chapters"></ol>
      </div>`;
    const themes = box.querySelector(".sw-tour__theme");
    for (const [id, label] of THEMES) {
      const b = el("button", "sw-tour__theme-btn", { type: "button", "data-theme": id, "aria-pressed": String(id === "dark") });
      b.textContent = label;
      themes.append(b);
    }
    return box;
  }

  function start(box) {
    const $ = s => box.querySelector(s);
    const frameBox = $(".sw-tour__frame");
    const playBtn = $(".sw-tour__play");
    const startBtn = $(".sw-tour__start");
    const range = $(".sw-tour__range");
    const time = $(".sw-tour__time");
    const log = $(".sw-tour__log");
    const ticks = $(".sw-tour__ticks");

    let P = null;
    let segs = [];
    let total = 0;
    let totalLabel = "0:00";
    let T = 0; // film time; total..total + HOLD is the hold
    let want = !reduceQuery.matches; // the visitor's play state
    let seen = false; // section in view
    let scrubbing = false;
    let theme = "dark";
    let shownScene = "";
    let drawnT = -1;
    let rowAt = -1;
    let rowDone = -1;
    let lastSec = -1;
    let last = 0;
    let raf = 0;
    let frame = null;

    const segAt = t => {
      for (const s of segs) if (t < s.start + s.dur) return s;
      return segs[segs.length - 1];
    };
    const playing = () => want && seen && !scrubbing && P;

    function setWant(v) {
      want = v;
      playBtn.classList.toggle("is-paused", !v);
      playBtn.setAttribute("aria-label", v ? "Pause the tour" : "Play the tour");
      if (v) startBtn.hidden = true;
      // Play from the end, or from a chapter's end frame (reduced motion),
      // replays: the film from 0, the chapter from its start.
      if (v && T >= total) T = 0;
      else if (v && segs.length) {
        const s = segAt(T);
        if (T >= s.start + s.dur - 1) T = s.start;
      }
      kick();
    }

    // Draws film time T: a new chapter is mounted only when it changes.
    function draw() {
      if (!P) return;
      const t = Math.min(T, total);
      const s = segAt(t);
      const local = Math.min(s.dur, t - s.start);
      if (s.id !== shownScene) {
        P.set({ scene: s.id, t: local });
        shownScene = s.id;
      } else if (local !== drawnT) {
        P.set({ t: local });
      }
      drawnT = local;
      range.value = String(Math.round(t));
      range.style.setProperty("--p", (t / total).toFixed(4));
      // The end counts as its own second: it can fall inside the last whole one.
      const sec = t >= total ? Infinity : Math.floor(t / 1000);
      if (sec !== lastSec) {
        lastSec = sec;
        // Whole seconds, rounded down; the end shows the length (0:44 / 0:44).
        const now = t >= total ? totalLabel : fmt(t);
        time.textContent = `${now} / ${totalLabel}`;
        range.setAttribute("aria-valuetext", `${now} of ${totalLabel}, chapter ${s.n}: ${s.title}`);
      }
      // Rows: current one, its progress, and every finished one's result. At
      // the end the last row stays open (and logged), so the log keeps its
      // height through the hold.
      const done = t >= total ? segs.length : s.n - 1;
      if (s.n !== rowAt || done !== rowDone) {
        rowAt = s.n;
        rowDone = done;
        segs.forEach((g, i) => {
          g.row.classList.toggle("is-current", g === s);
          g.row.classList.toggle("is-done", i < done);
          if (g === s) g.btn.setAttribute("aria-current", "step");
          else g.btn.removeAttribute("aria-current");
        });
      }
      s.row.style.setProperty("--p", (local / s.dur).toFixed(4));
    }

    function tick(now) {
      raf = 0;
      const dt = Math.min(100, now - last);
      last = now;
      if (!playing()) {
        box.classList.remove("is-playing");
        return;
      }
      T += dt;
      if (T >= total + HOLD) T = 0;
      draw();
      raf = requestAnimationFrame(tick);
    }
    function kick() {
      box.classList.toggle("is-playing", !!playing());
      if (playing() && !raf) {
        last = performance.now();
        raf = requestAnimationFrame(tick);
      }
    }
    function seek(t) {
      T = Math.max(0, Math.min(total, t));
      draw();
    }
    // To a chapter's start; with reduced motion while paused, to its end
    // frame, where its result shows.
    function jump(seg) {
      startBtn.hidden = true;
      seek(reduceQuery.matches && !want ? seg.start + seg.dur - 1 : seg.start);
    }

    function setTheme(id) {
      if (id === theme) return;
      theme = id;
      for (const b of box.querySelectorAll(".sw-tour__theme-btn")) b.setAttribute("aria-pressed", String(b.dataset.theme === id));
      if (!P) return;
      P.set({ theme: id, scene: shownScene || segs[0].id });
      drawnT = -1;
      draw();
    }

    function fallback() {
      frame?.remove();
      startBtn.hidden = true;
      $(".sw-tour__fallback").hidden = false;
      box.classList.add("is-failed");
    }

    function ready(player) {
      P = player;
      let at = 0;
      log.textContent = "";
      ticks.textContent = "";
      segs = P.scenes().map((s, i) => {
        const seg = { id: s.id, title: s.title, takeaway: s.takeaway, dur: s.duration, start: at, n: i + 1 };
        at += s.duration;
        const [result, kind] = RESULTS[s.id] || ["DONE", "info"];
        const row = el("li", "sw-tour__row", { "data-kind": kind });
        const btn = el("button", "sw-tour__row-btn", { type: "button" });
        btn.innerHTML = `
          <span class="sw-tour__num" aria-hidden="true"></span>
          <span class="sw-tour__at" aria-hidden="true"></span>
          <span class="sw-tour__name"></span>
          <span class="sw-tour__result"></span>
          <span class="sw-tour__take"><span><span></span></span></span>
          <span class="sw-tour__meter" aria-hidden="true"></span>`;
        btn.querySelector(".sw-tour__num").textContent = String(i + 1).padStart(2, "0");
        btn.querySelector(".sw-tour__at").textContent = fmt(seg.start);
        btn.querySelector(".sw-tour__name").textContent = s.title;
        btn.querySelector(".sw-tour__result").textContent = result;
        btn.querySelector(".sw-tour__take span span").textContent = s.takeaway || "";
        btn.addEventListener("click", () => jump(seg));
        row.append(btn);
        log.append(row);
        seg.row = row;
        seg.btn = btn;
        return seg;
      });
      total = at;
      totalLabel = fmt(Math.round(total / 1000) * 1000);
      for (const s of segs.slice(1)) {
        const tk = el("i");
        tk.style.left = `${(s.start / total) * 100}%`;
        ticks.append(tk);
      }
      range.max = String(total);
      box.classList.add("is-ready");
      if (theme !== "dark") P.set({ theme });
      // Click on the picture pauses or plays. The click focuses the frame, so
      // keys pressed there come here first and never reach the player's own
      // shortcuts, which would change its scene or clock behind this script.
      frame.contentDocument.addEventListener("click", () => setWant(!want));
      frame.contentWindow.addEventListener("keydown", e => {
        e.stopImmediatePropagation();
        if (e.key === " ") {
          e.preventDefault();
          setWant(!want);
        } else if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
          e.preventDefault();
          const next = segs[segs.indexOf(segAt(Math.min(T, total))) + (e.key === "ArrowRight" ? 1 : -1)];
          if (next) jump(next);
        }
      }, true);
      if (!want && reduceQuery.matches) {
        const poster = segs.find(s => s.id === POSTER.scene) || segs[0];
        T = poster.start + Math.round(poster.dur * POSTER.at);
        startBtn.hidden = false;
      }
      setWant(want);
      draw();
    }

    function load() {
      frame = el("iframe", "sw-tour__iframe", {
        src: SRC,
        title: "SecureWall tour: the app's windows replayed",
        tabindex: "-1",
        loading: "eager",
      });
      frameBox.prepend(frame);
      // The deadline runs from here, so a frame whose load never fires
      // also falls back.
      let failed = false;
      let poll = 0;
      const fail = () => {
        if (failed || P) return;
        failed = true;
        clearTimeout(poll);
        fallback();
      };
      const deadline = setTimeout(fail, 15000);
      frame.addEventListener("load", () => {
        const wait = () => {
          if (failed) return;
          let p = null;
          try {
            p = frame.contentWindow.__player;
          } catch {
            fail();
            return;
          }
          if (p && p.ready) {
            clearTimeout(deadline);
            if (p.errors.length || p.missing.length) console.warn("tour:", p.errors, p.missing);
            try {
              ready(p);
            } catch (err) {
              console.error("tour:", err);
              P = null;
              fail();
            }
          } else poll = setTimeout(wait, 50);
        };
        wait();
      });
    }

    playBtn.addEventListener("click", () => setWant(!want));
    startBtn.addEventListener("click", () => setWant(true));
    for (const b of box.querySelectorAll(".sw-tour__theme-btn")) b.addEventListener("click", () => setTheme(b.dataset.theme));
    range.addEventListener("pointerdown", () => {
      scrubbing = true;
      kick();
    });
    range.addEventListener("input", () => {
      seek(+range.value);
      if (!want && !startBtn.hidden) startBtn.hidden = true;
    });
    const endScrub = () => {
      if (!scrubbing) return;
      scrubbing = false;
      kick();
    };
    // Keys: the range steps by 1 ms so its end is the film's end; arrows move
    // 1 s and Page Up / Page Down 5 s instead.
    const KEY_STEP = { ArrowRight: 1000, ArrowUp: 1000, ArrowLeft: -1000, ArrowDown: -1000, PageUp: 5000, PageDown: -5000 };
    range.addEventListener("keydown", e => {
      if (!(e.key in KEY_STEP)) return;
      e.preventDefault();
      startBtn.hidden = true;
      seek(Math.min(T, total) + KEY_STEP[e.key]);
    });
    range.addEventListener("pointerup", endScrub);
    range.addEventListener("pointercancel", endScrub);
    range.addEventListener("change", endScrub);
    reduceQuery.addEventListener("change", e => { if (e.matches) setWant(false); });

    // Load the player when the section comes near; play while it is in view.
    // The site scrolls inside .scroll-container, which clips the page, so the
    // margin is measured against it (against the viewport it would not apply).
    const near = new IntersectionObserver(entries => {
      if (entries.some(e => e.isIntersecting)) {
        near.disconnect();
        load();
      }
    }, { root: box.closest(".scroll-container"), rootMargin: "800px 0px" });
    near.observe(box);
    new IntersectionObserver(entries => {
      seen = entries[entries.length - 1].isIntersecting;
      kick();
    }, { threshold: 0.35 }).observe(frameBox);
  }

  // The site renders #team after this script runs: wait for the download
  // grid and put the section after it, once.
  function place() {
    const grid = document.querySelector("#team .sw-download-grid");
    if (!grid) return false;
    const box = build();
    grid.after(box);
    start(box);
    return true;
  }
  if (!place()) {
    const watch = new MutationObserver(() => {
      if (place()) watch.disconnect();
    });
    watch.observe(document.documentElement, { childList: true, subtree: true });
  }
})();
