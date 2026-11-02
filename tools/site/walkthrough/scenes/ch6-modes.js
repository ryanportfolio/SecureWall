// Chapter 6: change the firewall mode from the tray.
// Screens: tray menu and its Change mode submenu (TinyWallController.cs InitializeComponent,
// MainForm.resx; refs tray.png, tray-menu.png); tray icon per mode (UpdateDisplay); notification
// "The firewall is now blocking all inbound and outbound traffic." (Messages.resx:208, SetMode).
// Prompts appear only in Normal protection (architecture.md), which the takeaway states.
// Beat sheet (ms):
//   0     desktop, green shield in the tray
//   700   pointer to the shield; 1500 click: the tray menu opens above the taskbar
//   2300  pointer onto Change mode: highlight, submenu opens to the left
//   3300  pointer into the submenu onto Block all; 3900 click
//   4050  menus close; the tray shield turns yellow; the notification slides in
//   5121  end (the first 1085 ms are trimmed)
(function () {
  const T = { move: 700, tapTray: 1500, open: 1600, onMode: 2300, sub: 2450, onBlock: 3300, tap: 3900, close: 4050, toast: 4300, dur: 5121, trim: 1085};
  (window.SCENES = window.SCENES || []).push({
    id: "ch6-modes", order: 6,
    title: "Change the firewall mode from the tray",
    takeaway: "Pick a mode from the tray icon; prompts appear only in Normal protection.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "The pointer clicks the green SecureWall shield in the Windows tray. In the menu it opens Change mode and picks Block all. The shield turns yellow and a notification says the firewall now blocks all inbound and outbound traffic.",
    duration: T.dur - T.trim,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.menu({ id: "tray-menu", x: 1076 - 249, bottom: 680, w: 249, items: U.trayItems() })}
        <div data-sw="sub-host"></div>
        ${U.toast({ x: 1200 - 364 - 12, y: 568, text: "The firewall is now blocking all inbound and outbound traffic." })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), menu = $('[data-sw="tray-menu"]'), modeItem = $('[data-sw="mi-mode"]'), toast = $('[data-sw="toast"]');
      const trayImg = $('[data-sw="tray-shield-img"]'), trayIcon = $('[data-sw="tray-shield"]');
      const mi = U.local(modeItem);
      $('[data-sw="sub-host"]').outerHTML = U.menu({ id: "mode-menu", x: mi.x - 173 + 2, y: mi.y - 3, w: 173, items: U.MODE_ITEMS("") });
      const sub = $('[data-sw="mode-menu"]'), block = $('[data-sw="mm-blockall"]');
      const cam = U.cam(1.5, 900, 520);
      camEl.style.transform = cam.css;

      const S = 34, aim = (x, y) => { const m = cam.map(x, y); return ctx.aim(m.x, m.y, S); };
      const tr = U.local(trayIcon), bl = U.local(block);
      const pTray = aim(tr.cx, tr.cy), pMode = aim(mi.x + 170, mi.cy + 3), pBlock = aim(bl.x + 120, bl.cy + 3);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 560, y: 300, size: S },
        { t: T.move, x: 560, y: 300 },
        { t: T.tapTray - 220, x: pTray.x, y: pTray.y, ease: "out" },
        { t: T.tapTray, tap: true }, { t: T.tapTray + 180, tap: false },
        { t: T.onMode, x: pMode.x, y: pMode.y, ease: "inOut" },
        { t: T.onMode + 500, x: pMode.x - 30, y: pMode.y },
        { t: T.onBlock, x: pBlock.x, y: pBlock.y, ease: "inOut" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.close + 900, x: pBlock.x - 60, y: pBlock.y - 150, ease: "inOut" },
        { t: T.dur, x: pBlock.x - 64, y: pBlock.y - 152 },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          const open = t >= T.open && t < T.close;
          set(menu, "visibility", open ? "visible" : "hidden");
          set(modeItem, "class", "sw-mi sw-sub" + (t >= T.onMode - 120 ? " sw-hover" : ""));
          set(sub, "visibility", open && t >= T.sub ? "visible" : "hidden");
          set(block, "class", "sw-mi" + (t >= T.onBlock - 120 ? " sw-hover" : ""));
          set(trayIcon, "class", "sw-tray-icon" + (t >= T.tapTray - 220 && t < T.close ? " sw-hot" : ""));
          const src = U.shieldSrc(t >= T.close ? "blockall" : "normal");
          if (trayImg.getAttribute("src") !== src) trayImg.setAttribute("src", src);
          const ti = OB.ease("out")(OB.prog(t, T.toast, 320));
          set(toast, "visibility", t >= T.toast ? "visible" : "hidden");
          set(toast, "transform", `translateX(${((1 - ti) * 380).toFixed(1)}px)`);
        },
        destroy() {},
      };
    },
  });
})();
