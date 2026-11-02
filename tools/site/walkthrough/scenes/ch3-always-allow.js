// Chapter 3: Always allow a signed app by file name and publisher, in any folder.
// Screens: BlockedConnectionPopup with Always allow (BlockedConnectionPopup.AlwaysAllow.cs, shown
// only for executables with a valid embedded signature; ref prompt-notesync.png); Manage, Always
// allow tab (SettingsForm.AlwaysAllow.cs, ref manage-tab4-*.png).
// Beat sheet (ms):
//   0     popup for NoteSync.exe in a per-version AppData folder; the folder warning; Allow armed
//   900   pointer travels to Always allow
//   2200  pointer rests: the button's tooltip explains the rule (AlwaysAllow.cs:57)
//   5000  click; 5150 popup closes
//   5500  camera eases to the centre; Manage opens on the Always allow tab with the rule
//   6300  pointer clicks the NoteSync.exe rule; it selects and Remove enables
//   7103  end (the first 103 ms are trimmed)
(function () {
  const T = { move: 900, tip: 2200, tap: 5000, close: 5150, pan: 5300, open: 5500, mark: 6300, dur: 7103, trim: 103};
  const WARN = ["writable"];
  const TIP = "Allow now, and from now on allow any file named NoteSync.exe signed by NoteSync Labs Inc., in any folder. Outbound TCP/UDP only.";
  (window.SCENES = window.SCENES || []).push({
    id: "ch3-always-allow", order: 3,
    title: "Always allow a signed app, in any folder",
    takeaway: "For signed apps: allow by file name and publisher, wherever it updates to.",
    cta: { label: "Read the prompt guide", href: "/#public-goods" },
    alt: "A SecureWall popup for NoteSync.exe offers Always allow. Its tooltip says any file named NoteSync.exe signed by NoteSync Labs Inc. is allowed in any folder, outbound TCP/UDP only. The pointer clicks Always allow, and the Manage window lists the rule on its Always allow tab.",
    duration: T.dur - T.trim,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const ph = U.popupHeight(WARN), px = 1200 - 460 - 16, py = 680 - ph - 16;
      const mx = 225, my = 112;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.popup({ x: px, y: py, name: "NoteSync.exe", path: U.P.notesync, armed: true, alwaysAllow: true, warnings: WARN })}
        ${U.tip({ x: px + 60, y: py + 222 + 4 + 15 * WARN.length - 62, w: 330, text: TIP })}
        ${U.manage({ x: mx, y: my, tab: "Always allow", listId: "aa-list", rows: [{ id: "aa-notesync", noicon: true, cells: ["NoteSync.exe", "NoteSync Labs Inc."] }] })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), pop = $('[data-sw="popup"]'), always = $('[data-sw="pop-always"]'), tip = $('[data-sw="tip"]');
      const win = $('[data-sw="manage"]'), rowEl = $('[data-sw="aa-notesync"]'), remove = $('[data-sw="tab-remove"]');
      const camA = U.cam(1.5, 954, 530), camB = U.cam(1.32, mx + 375, my + 250);

      const S = 34, aim = (c, x, y) => { const m = c.map(x, y); return ctx.aim(m.x, m.y, S); };
      const b = U.local(always), pBtn = aim(camA, b.x + b.w - 10, b.cy + 8);
      const r = U.local(rowEl), pRow = aim(camB, r.x + 150, r.y + 9);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 560, y: 300, size: S },
        { t: T.move, x: 560, y: 300 },
        { t: T.tip - 300, x: pBtn.x, y: pBtn.y, ease: "out" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.close + 200, x: pBtn.x, y: pBtn.y },
        { t: T.mark - 150, x: pRow.x, y: pRow.y, ease: "out" },
        { t: T.mark, tap: true }, { t: T.mark + 180, tap: false },
        { t: T.mark + 900, x: pRow.x + 60, y: pRow.y + 70, ease: "inOut" },
        { t: T.dur, x: pRow.x + 64, y: pRow.y + 72 },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          const u = OB.ease("inOut")(OB.prog(t, T.pan, 700));
          set(camEl, "transform", U.camMix(camA, camB, u).css);
          set(pop, "visibility", t < T.close ? "visible" : "hidden");
          const hover = t >= T.tip - 300 && t < T.close, press = t >= T.tap && t < T.tap + 180;
          set(always, "class", "sw-pop-btn sw-pop-always" + (press ? " sw-press" : hover ? " sw-hover" : ""));
          set(tip, "visibility", t >= T.tip && t < T.tap ? "visible" : "hidden");
          U.openFx(set, win, t, T.open);
          set(rowEl, "class", "sw-lrow" + (t >= T.mark ? " sw-sel" : ""));
          set(remove, "class", "sw-btn" + (t >= T.mark ? "" : " sw-disabled"));
        },
        destroy() {},
      };
    },
  });
})();
