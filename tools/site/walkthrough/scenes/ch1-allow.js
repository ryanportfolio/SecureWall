// Chapter 1: allow an app from the blocked-connection popup.
// Screens: BlockedConnectionPopup (BlockedConnectionPopup.Designer.cs), ref prompt-inkwell.png.
// Camera: 1.5x on the bottom-right of the screen, where the popup opens.
// Beat sheet (ms):
//   0     desktop at rest; pointer idle
//   500   popup appears 16 px from the working area's corner; Allow outgoing disabled
//   1500  Allow enables: shown and unmoved for 1 s (PromptAllowArming.ArmDelay)
//   1800  pointer travels to Allow outgoing
//   3300  click
//   3500  rule saved, popup closes (PromptDisplayCoordinator.CompleteCurrent)
//   6500  end
(function () {
  const T = { show: 500, arm: 1500, move: 1800, tap: 3300, close: 3500, dur: 6500 };
  (window.SCENES = window.SCENES || []).push({
    id: "ch1-allow", order: 1,
    title: "Allow an app the first time it connects",
    takeaway: "New app blocked? Click Allow outgoing once and it stays allowed.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "On a Windows desktop, a SecureWall popup reports that Inkwell.exe was blocked connecting out. After a second its Allow outgoing button enables, the pointer clicks it, and the popup closes.",
    duration: T.dur,
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const px = 1200 - 460 - 16, py = 680 - U.popupHeight() - 16;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.popup({ x: px, y: py, name: "Inkwell.exe", path: U.P.inkwell, armed: true })}</div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), pop = $('[data-sw="popup"]'), allow = $('[data-sw="pop-allow"]');
      const cam = U.cam(1.5, 954, 530);
      camEl.style.transform = cam.css;

      const S = 34, aim = (x, y) => { const m = cam.map(x, y); return ctx.aim(m.x, m.y, S); };
      const a = U.local(allow), pAllow = aim(a.x + a.w - 12, a.cy + 8);
      const rest = aim(a.x - 120, a.y - 150);
      ctx.actor([
        { t: 0, x: 520, y: 260, size: S },
        { t: T.move, x: 520, y: 260 },
        { t: T.tap - 250, x: pAllow.x, y: pAllow.y, ease: "out" },
        { t: T.tap, tap: true }, { t: T.tap + 180, tap: false },
        { t: T.close + 700, x: rest.x, y: rest.y, ease: "inOut" },
        { t: T.dur, x: rest.x - 4, y: rest.y - 3 },
      ]);

      const set = OB.writer();
      return {
        frame(t) {
          U.openFx(set, pop, t, T.show, T.close);
          const hover = t >= T.tap - 250 && t < T.close, press = t >= T.tap && t < T.tap + 180;
          set(allow, "class", "sw-pop-btn sw-pop-allow" + (t >= T.arm ? "" : " sw-disabled") + (press ? " sw-press" : hover ? " sw-hover" : ""));
        },
        destroy() {},
      };
    },
  });
})();
