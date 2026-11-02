// Chapter 8: check for updates from the tray and install the newer release.
// Screens: tray menu "Check for updates..." (TinyWallController.Updates.cs:22); UpdateDialog
// (UpdateDialog.cs) offering 0.5.2, then downloading (Updates.cs:156); refs update*.png.
// The check runs only when clicked; the MSI is checked against SHA256SUMS.txt (README).
// Beat sheet (ms):
//   0     desktop; 500 pointer to the shield; 1000 click, menu opens
//   1600  pointer onto Check for updates...; 2000 click, menu closes
//   2300  camera eases to the centre; the update window opens: "SecureWall 0.5.2 is available"
//   4200  pointer to Install; 4900 click
//   5100  "Downloading SecureWall 0.5.2", the progress bar fills
//   10000 end (download still running: the elevation prompt and setup are not shown)
(function () {
  const T = { move: 500, tapTray: 1000, open: 1100, onItem: 1600, tapItem: 2000, pan: 2100, win: 2400, move2: 4200, tapInstall: 4900, dl: 5100, dur: 10000 };
  (window.SCENES = window.SCENES || []).push({
    id: "ch8-update", order: 8,
    title: "Update from the tray",
    takeaway: "Check for updates downloads, verifies and installs the newest release in place.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "From the SecureWall tray menu the pointer clicks Check for updates. A window says SecureWall 0.5.2 is available, with its release notes. The pointer clicks Install and the window shows the download progressing.",
    duration: T.dur,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const wx = 299, wy = 100;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.update({ id: "up-offer", x: wx, y: wy })}
        ${U.update({ id: "up-dl", x: wx, y: wy, state: "downloading", progress: 0 })}
        ${U.menu({ id: "tray-menu", x: 1076 - 249, bottom: 680, w: 249, items: U.trayItems() })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), menu = $('[data-sw="tray-menu"]'), item = $('[data-sw="mi-update"]'), trayIcon = $('[data-sw="tray-shield"]');
      const offer = $('[data-sw="up-offer"]'), dl = $('[data-sw="up-dl"]'), install = $('[data-sw="up-install"]'), bar = $('[data-sw="progress"]');
      const camA = U.cam(1.5, 900, 500), camB = U.cam(1.15, wx + 301, wy + 266);

      const S = 34, aim = (c, x, y) => { const m = c.map(x, y); return ctx.aim(m.x, m.y, S); };
      const tr = U.local(trayIcon), it = U.local(item), ins = U.local(install);
      const pTray = aim(camA, tr.cx, tr.cy), pItem = aim(camA, it.x + 70, it.cy), pInst = aim(camB, ins.x + ins.w - 8, ins.cy + 5);
      ctx.actor([
        { t: 0, x: 560, y: 260, size: S },
        { t: T.move, x: 560, y: 260 },
        { t: T.tapTray - 220, x: pTray.x, y: pTray.y, ease: "out" },
        { t: T.tapTray, tap: true }, { t: T.tapTray + 180, tap: false },
        { t: T.onItem, x: pItem.x, y: pItem.y, ease: "inOut" },
        { t: T.tapItem, tap: true }, { t: T.tapItem + 180, tap: false },
        { t: T.move2, x: pItem.x - 40, y: pItem.y + 30, ease: "inOut" },
        { t: T.tapInstall - 250, x: pInst.x, y: pInst.y, ease: "out" },
        { t: T.tapInstall, tap: true }, { t: T.tapInstall + 180, tap: false },
        { t: T.dl + 800, x: pInst.x + 120, y: pInst.y + 60, ease: "inOut" },
        { t: T.dur, x: pInst.x + 124, y: pInst.y + 62 },
      ]);

      const set = OB.writer();
      return {
        frame(t) {
          const u = OB.ease("inOut")(OB.prog(t, T.pan, 700));
          set(camEl, "transform", U.camMix(camA, camB, u).css);
          set(menu, "visibility", t >= T.open && t < T.tapItem + 120 ? "visible" : "hidden");
          set(item, "class", "sw-mi" + (t >= T.onItem - 120 ? " sw-hover" : ""));
          set(trayIcon, "class", "sw-tray-icon" + (t >= T.tapTray - 220 && t < T.tapItem + 120 ? " sw-hot" : ""));
          U.openFx(set, offer, t, T.win, T.dl);
          const hover = t >= T.tapInstall - 250, press = t >= T.tapInstall && t < T.tapInstall + 180;
          set(install, "class", "sw-btn sw-default" + (press ? " sw-press" : hover ? " sw-hover" : ""));
          set(dl, "visibility", t >= T.dl ? "visible" : "hidden");
          // Download progress: the eased share of a 9.8 MB installer, still running at the end.
          const p = 0.72 * OB.ease("out")(OB.prog(t, T.dl + 200, T.dur - T.dl - 200));
          set(bar, "width", Math.round(576 * p) + "px");
        },
        destroy() {},
      };
    },
  });
})();
