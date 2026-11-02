// Chapter 4: Network Activity shows each connection; a blocked row can be unblocked from its menu.
// Screens: ConnectionsForm (ConnectionsForm.Designer.cs / .resx, ref na-*.png) and its row menu
// (ref na-*-menu.png); the Windows notification from AddExceptions (Messages.resx:251).
// Unblock saves the app database's exception for the file: for an unknown app that is
// unrestricted TCP/UDP (AppDatabase.cs:115-118), so nothing here calls it outbound-only.
// Camera: 1.12x on the window; slides right for the notification (it opens at the screen's corner).
// Beat sheet (ms, before the 93 ms trim):
//   0     Network Activity open, rows newest first; PhotoDrop.exe is "Blocked (default deny)"
//   800   pointer travels to the PhotoDrop.exe row
//   2200  right-click: the row selects, its menu opens at the pointer
//   2900  pointer slides onto Unblock (highlight); 3400 click, menu closes
//   3800  camera slides right; the notification slides in at the bottom right
//   4629  end (the first 93 ms are trimmed)
(function () {
  const T = { move: 800, rclick: 2200, onItem: 2900, tap: 3400, toast: 3800, dur: 4629, trim: 93 };
  (window.SCENES = window.SCENES || []).push({
    id: "ch4-activity", order: 4,
    title: "Unblock an app from Network Activity",
    takeaway: "Every connection, refreshed each second. Right-click a blocked row to unblock it.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "The Network Activity window lists connections with their state. The pointer right-clicks PhotoDrop.exe, marked Blocked (default deny), and chooses Unblock. A Windows notification says PhotoDrop.exe's firewall rules changed.",
    duration: T.dur - T.trim,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const wx = 12, wy = 30;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.networkActivity({ x: wx, y: wy })}
        <div data-sw="menu-host"></div>
        ${U.toast({ x: 1200 - 364 - 12, y: 548, text: "Firewall rules for unrecognized PhotoDrop.exe have been changed. Click this popup to edit the exception." })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const rowEl = $('[data-sw="na-photodrop"]'), toast = $('[data-sw="toast"]');
      const r = U.local(rowEl);
      const click = { x: r.x + 62, y: r.y + 9 };
      $('[data-sw="menu-host"]').outerHTML = U.menu({ id: "na-menu", x: click.x, y: click.y, w: 187, themed: true, items: U.NA_MENU("") });
      const menu = $('[data-sw="na-menu"]'), unblock = $('[data-sw="nm-unblock"]');

      const camA = U.cam(1.12, 0, 0), camB = U.cam(1.12, 1200, 0);
      const camEl = $(".sw-cam");
      const S = 34, aim = (x, y) => { const m = camA.map(x, y); return ctx.aim(m.x, m.y, S); };
      const ub = U.local(unblock);
      const pRow = aim(click.x, click.y), pUn = aim(ub.x + 120, ub.cy + 3);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 700, y: 420, size: S },
        { t: T.move, x: 700, y: 420 },
        { t: T.rclick - 200, x: pRow.x, y: pRow.y, ease: "out" },
        { t: T.rclick, tap: true }, { t: T.rclick + 180, tap: false },
        { t: T.onItem, x: pUn.x, y: pUn.y, ease: "inOut" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.dur, x: pUn.x + 300, y: pUn.y + 130, ease: "inOut" },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          set(camEl, "transform", U.camMix(camA, camB, OB.ease("inOut")(OB.prog(t, T.toast - 100, 600))).css);
          set(rowEl, "class", "sw-lrow" + (t >= T.rclick ? " sw-sel" : ""));
          set(menu, "visibility", t >= T.rclick && t < T.tap + 100 ? "visible" : "hidden");
          set(unblock, "class", "sw-mi" + (t >= T.onItem - 150 ? " sw-hover" : ""));
          const ti = OB.ease("out")(OB.prog(t, T.toast, 320));
          set(toast, "visibility", t >= T.toast ? "visible" : "hidden");
          set(toast, "transform", `translateX(${((1 - ti) * 380).toFixed(1)}px)`);
        },
        destroy() {},
      };
    },
  });
})();
