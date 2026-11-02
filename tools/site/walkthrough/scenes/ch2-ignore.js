// Chapter 2: Ignore keeps an app blocked and stops the prompts; it is listed under Manage, Ignored.
// Screens: BlockedConnectionPopup with the three risk lines (BlockedConnectionPopup.Risk.cs,
// ref prompt-telemetry.png); Manage, Ignored tab (SettingsForm.Ignored.cs, ref manage-tab5-*.png).
// Beat sheet (ms):
//   0     popup for telemetry_agent.exe already open, warnings shown, Allow armed
//   900   pointer travels to Ignore
//   2600  click; 2750 popup closes (IGNORE_PROMPT saved the identity)
//   3300  camera eases to the screen centre; Manage opens on the Ignored tab
//   4100  pointer clicks the telemetry_agent.exe row: it selects and Remove enables; pointer rests beside it
//   5153  end (the first 123 ms are trimmed)
(function () {
  const T = { move: 900, tap: 2600, close: 2750, pan: 3000, open: 3300, mark: 4100, dur: 5153, trim: 123};
  const WARN = ["unsigned", "writable", "recent"];
  (window.SCENES = window.SCENES || []).push({
    id: "ch2-ignore", order: 2,
    title: "Keep an app blocked and stop being asked",
    takeaway: "Ignore leaves it blocked and lists it under Manage, Ignored.",
    cta: { label: "Read the prompt guide", href: "/#public-goods" },
    alt: "A SecureWall popup reports that telemetry_agent.exe, unsigned and in a user-writable folder, was blocked. The pointer clicks Ignore and the popup closes. The Manage window then shows telemetry_agent.exe on its Ignored tab.",
    duration: T.dur - T.trim,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const ph = U.popupHeight(WARN), px = 1200 - 460 - 16, py = 680 - ph - 16;
      const mx = 225, my = 112;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.popup({ x: px, y: py, name: "telemetry_agent.exe", path: U.P.telemetry, armed: true, warnings: WARN })}
        ${U.manage({ x: mx, y: my, tab: "Ignored", listId: "ign-list", rows: [{ id: "ign-telemetry", noicon: true, cells: ["telemetry_agent.exe", U.P.telemetry] }] })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), pop = $('[data-sw="popup"]'), ignore = $('[data-sw="pop-ignore"]');
      const win = $('[data-sw="manage"]'), rowEl = $('[data-sw="ign-telemetry"]'), remove = $('[data-sw="tab-remove"]');
      const camA = U.cam(1.5, 954, 530), camB = U.cam(1.32, mx + 375, my + 250);

      const S = 34, aim = (c, x, y) => { const m = c.map(x, y); return ctx.aim(m.x, m.y, S); };
      const g = U.local(ignore), pIgn = aim(camA, g.x + g.w - 9, g.cy + 8);
      const r = U.local(rowEl), pRow = aim(camB, r.x + 150, r.y + 9);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 560, y: 300, size: S },
        { t: T.move, x: 560, y: 300 },
        { t: T.tap - 250, x: pIgn.x, y: pIgn.y, ease: "out" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.close + 200, x: pIgn.x, y: pIgn.y },
        { t: T.mark - 150, x: pRow.x, y: pRow.y, ease: "out" },
        { t: T.mark, tap: true }, { t: T.mark + 180, tap: false },
        { t: T.mark + 900, x: pRow.x + 40, y: pRow.y + 60, ease: "inOut" },
        { t: T.dur, x: pRow.x + 44, y: pRow.y + 62 },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          const u = OB.ease("inOut")(OB.prog(t, T.pan, 700));
          set(camEl, "transform", U.camMix(camA, camB, u).css);
          set(pop, "visibility", t < T.close ? "visible" : "hidden");
          const hover = t >= T.tap - 250 && t < T.close, press = t >= T.tap && t < T.tap + 180;
          set(ignore, "class", "sw-pop-btn sw-pop-ignore" + (press ? " sw-press" : hover ? " sw-hover" : ""));
          U.openFx(set, win, t, T.open);
          set(rowEl, "class", "sw-lrow" + (t >= T.mark ? " sw-sel" : ""));
          set(remove, "class", "sw-btn" + (t >= T.mark ? "" : " sw-disabled"));
        },
        destroy() {},
      };
    },
  });
})();
