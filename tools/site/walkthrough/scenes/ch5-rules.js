// Chapter 5: the Filter box narrows Manage's exception list as you type.
// Screen: SettingsForm, Application Exceptions tab (SettingsForm.resx, ref manage-tab1-*.png).
// The filter keeps rows whose Application or Type column contains the text, ignoring case
// (SettingsForm.cs ApplyExceptionFilter).
// Beat sheet (ms):
//   0     Manage open on Application Exceptions, three rules
//   700   pointer travels to Filter; 1600 click, caret blinks
//   2100  "note" types in; the list narrows on each letter (NoteSync.exe stays)
//   3700  pointer moves to the remaining row and clicks it; Modify and Remove enable
//   5199  end (the first 109 ms are trimmed)
(function () {
  const T = { move: 700, tapFilter: 1600, type: 2100, cps: 4, tapRow: 4000, dur: 5199, trim: 109};
  const WORD = "note";
  (window.SCENES = window.SCENES || []).push({
    id: "ch5-rules", order: 5,
    title: "Find any rule in Manage",
    takeaway: "Type in Filter to narrow the exception list by app name.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "The Manage window lists three application exceptions. The pointer clicks the Filter box and types note; the list narrows to NoteSync.exe, which the pointer then selects.",
    duration: T.dur - T.trim,
    trim: T.trim, // ms cut from the start of the beat sheet (film.html exports beat-sheet times)
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const mx = 225, my = 112;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.manage({ x: mx, y: my, tab: "Application Exceptions", filterFocus: false, caret: true })}</div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), filter = $('[data-sw="filter"]'), ftext = $('[data-sw="filter-text"]'), caret = $('[data-sw="caret"]');
      const rowsEl = $('[data-sw="ex-list-rows"]');
      const modify = [...stage.querySelectorAll(".sw-btn")].find((b) => b.textContent.trim() === "Modify");
      const remove = [...stage.querySelectorAll(".sw-btn")].find((b) => b.textContent.trim() === "Remove");
      const cam = U.cam(1.35, mx + 300, my + 230);
      camEl.style.transform = cam.css;

      const S = 34, aim = (x, y) => { const m = cam.map(x, y); return ctx.aim(m.x, m.y, S); };
      const f = U.local(filter), first = U.local(rowsEl);
      const pF = aim(f.x + 160, f.cy + 2), pRow = aim(first.x + 112, first.y + 9);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 900, y: 560, size: S },
        { t: T.move, x: 900, y: 560 },
        { t: T.tapFilter - 220, x: pF.x, y: pF.y, ease: "out" },
        { t: T.tapFilter, tap: true }, { t: T.tapFilter + 180, tap: false },
        { t: T.type - 100, x: pF.x + 40, y: pF.y + 70, ease: "inOut" },   // off the text while it types
        { t: T.tapRow - 220, x: pRow.x, y: pRow.y, ease: "inOut" },
        { t: T.tapRow, tap: true }, { t: T.tapRow + 180, tap: false },
        { t: T.tapRow + 900, x: pRow.x + 70, y: pRow.y + 80, ease: "inOut" },
        { t: T.dur, x: pRow.x + 74, y: pRow.y + 82 },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          const focused = t >= T.tapFilter;
          set(filter, "class", "sw-text" + (focused ? " sw-focus" : ""));
          const typed = OB.typed(WORD, t, T.type, T.cps);
          set(ftext, "text", typed);
          set(caret, "opacity", focused && Math.floor((t - T.tapFilter) / 530) % 2 === 0 ? "1" : "0");
          const q = typed.toUpperCase();
          const sel = t >= T.tapRow;
          const rows = U.EX_ROWS.filter((r) => !q || r.cells[0].toUpperCase().includes(q) || r.cells[1].toUpperCase().includes(q))
            .map((r) => Object.assign({}, r, { sel: sel && r.id === "ex-notesync" }));
          set(rowsEl, "html", rows.map((r) => U.row(U.EX_COLS, r)).join(""));
          set(modify, "class", "sw-btn sw-flat" + (sel ? "" : " sw-disabled"));
          set(remove, "class", "sw-btn sw-flat" + (sel ? "" : " sw-disabled"));
        },
        destroy() {},
      };
    },
  });
})();
