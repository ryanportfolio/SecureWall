// Chapter 4: Network Activity shows each connection; a blocked row can be unblocked from its menu.
// Screens: ConnectionsForm (ConnectionsForm.Designer.cs / .resx, ref na-*.png) and its row menu
// (ref na-*-menu.png); the Windows notification from AddExceptions (Messages.resx:251).
// Unblock saves the app database's exception for the file: for an unknown app that is
// unrestricted TCP/UDP (AppDatabase.cs:115-118), so nothing here calls it outbound-only.
// Camera: 1.12x on the window; slides right for the notification, then back.
// Beat sheet (ms):
//   0     Network Activity open, rows newest first; PhotoDrop.exe is "Blocked (default deny)"
//   800   pointer travels to the PhotoDrop.exe row
//   2200  right-click: the row selects, its menu opens at the pointer
//   2900  pointer slides onto Unblock (highlight); 3400 click, menu closes
//   3800  camera slides right; the notification slides in at the bottom right (shown 5 s,
//         the balloon period AddExceptions passes)
//   8800  notification slides out; camera slides back
//   9500  a refresh: PhotoDrop.exe's new connection lands on top as "Allowed"
//   12500 end
(function () {
  const T = { move: 800, rclick: 2200, onItem: 2900, tap: 3400, toast: 3800, toastOut: 8800, land: 9500, dur: 12500 };
  (window.SCENES = window.SCENES || []).push({
    id: "ch4-activity", order: 4,
    title: "Unblock an app from Network Activity",
    takeaway: "Every connection, refreshed each second. Right-click a blocked row to unblock it.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "The Network Activity window lists connections with their state. The pointer right-clicks PhotoDrop.exe, marked Blocked (default deny), and chooses Unblock. A Windows notification says PhotoDrop.exe's firewall rules changed, and a new PhotoDrop.exe row appears at the top as Allowed.",
    duration: T.dur,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const wx = 12, wy = 30;
      const fresh = { id: "na-photodrop-new", cells: ["PhotoDrop.exe (8816)", "", "TCP", "51622", "192.168.1.20", "  443", "198.51.100.23 (upload.photodrop.example)", "Allowed", "Out", "2026/11/01 10:24:34"] };
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.networkActivity({ x: wx, y: wy, rows: [fresh].concat(U.NA_ROWS) })}
        <div data-sw="menu-host"></div>
        ${U.toast({ x: 1200 - 364 - 12, y: 548, text: "Firewall rules for unrecognized PhotoDrop.exe have been changed. Click this popup to edit the exception." })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const rowEl = $('[data-sw="na-photodrop"]'), newRow = $('[data-sw="na-photodrop-new"]'), toast = $('[data-sw="toast"]');
      newRow.style.overflow = "hidden"; newRow.style.height = "0px"; newRow.style.borderBottomWidth = "0px";
      const r = U.local(rowEl);
      const click = { x: r.x + 62, y: r.y + 9 };
      $('[data-sw="menu-host"]').outerHTML = U.menu({ id: "na-menu", x: click.x, y: click.y, w: 187, themed: true, items: U.NA_MENU("") });
      const menu = $('[data-sw="na-menu"]'), unblock = $('[data-sw="nm-unblock"]');

      // 1.12x keeps the whole window in view; the camera slides right while the notification
      // shows (it opens at the screen's corner), then back for the new row.
      const camA = U.cam(1.12, 0, 0), camB = U.cam(1.12, 1200, 0);
      const camEl = $(".sw-cam");
      const S = 34, aim = (x, y) => { const m = camA.map(x, y); return ctx.aim(m.x, m.y, S); };
      const ub = U.local(unblock);
      const pRow = aim(click.x, click.y), pUn = aim(ub.x + 120, ub.cy + 3);
      ctx.actor([
        { t: 0, x: 700, y: 420, size: S },
        { t: T.move, x: 700, y: 420 },
        { t: T.rclick - 200, x: pRow.x, y: pRow.y, ease: "out" },
        { t: T.rclick, tap: true }, { t: T.rclick + 180, tap: false },
        { t: T.onItem, x: pUn.x, y: pUn.y, ease: "inOut" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.tap + 900, x: pUn.x + 380, y: pUn.y + 150, ease: "inOut" },
        { t: T.land - 200, x: pUn.x + 200, y: pUn.y + 120, ease: "inOut" },
        { t: T.dur, x: pUn.x + 204, y: pUn.y + 122 },
      ]);

      const set = OB.writer();
      return {
        frame(t) {
          const out = OB.ease("inOut")(OB.prog(t, T.toast - 100, 600)), back = OB.ease("inOut")(OB.prog(t, T.toastOut + 100, 600));
          set(camEl, "transform", U.camMix(camA, camB, out * (1 - back)).css);
          const sel = t >= T.rclick;
          set(rowEl, "class", "sw-lrow" + (sel ? " sw-sel" : ""));
          set(menu, "visibility", t >= T.rclick && t < T.tap + 100 ? "visible" : "hidden");
          set(unblock, "class", "sw-mi" + (t >= T.onItem - 150 ? " sw-hover" : ""));
          const ti = OB.ease("out")(OB.prog(t, T.toast, 320)) - OB.ease("in")(OB.prog(t, T.toastOut, 300));
          set(toast, "visibility", t >= T.toast && t < T.toastOut + 300 ? "visible" : "hidden");
          set(toast, "transform", `translateX(${((1 - ti) * 380).toFixed(1)}px)`);
          const grow = OB.ease("inOut")(OB.prog(t, T.land, 260));
          set(newRow, "height", (17 * grow).toFixed(2) + "px");
          set(newRow, "borderBottomWidth", grow > 0 ? "1px" : "0px");
          set(newRow, "opacity", grow.toFixed(3));
        },
        destroy() {},
      };
    },
  });
})();
