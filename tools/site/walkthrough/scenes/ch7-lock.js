// Chapter 7: lock SecureWall; allowing an app then asks for the password.
// Screens: tray menu with Lock (shown only when a password is set, TinyWallController.cs:304-321);
// popup for PhotoDrop.exe (ref prompt-photodrop.png); PasswordForm (PasswordForm.resx, ref
// password*.png), which the prompt's Allow opens while locked (UnlockForPrompt, :1165-1169).
// Beat sheet (ms):
//   0     desktop; 600 pointer to the shield; 1100 click, menu opens
//   1700  pointer onto Lock; 2100 click: menu closes (locked; Lock leaves the menu)
//   2900  popup for PhotoDrop.exe appears; 3900 Allow arms
//   4700  click Allow outgoing; 5000 the popup says SecureWall is locked and Allow enables again
//         (PromptDisplayCoordinator.cs ShowActionFailure(Locked) before TryUnlock); the password
//         dialog opens in front
//   5600  the passphrase types in as dots
//   6041 end (the first 751 ms are trimmed)
(function () {
  const T = { move: 400, tapTray: 1100, open: 1200, onLock: 1700, tapLock: 2100, show: 2900, arm: 3900, tapAllow: 4700, pw: 5000, type: 5600, dur: 6041, trim: 751};
  (window.SCENES = window.SCENES || []).push({
    id: "ch7-lock", order: 7,
    title: "Lock SecureWall with a password",
    takeaway: "With a password set, lock from the tray; changes then need it.",
    cta: { label: "Get SecureWall", href: "/#team" },
    alt: "The pointer opens the SecureWall tray menu and clicks Lock. When a popup then asks about PhotoDrop.exe and the pointer clicks Allow outgoing, the popup says SecureWall is locked and a Password - SecureWall dialog asks for the passphrase, and dots type into it.",
    duration: T.dur - T.trim,
    trim: T.trim, // ms cut from the start of the beat sheet (film.html exports beat-sheet times)
    mount(root, ctx) {
      const U = window.SWUI, { OB } = ctx;
      const px = 1200 - 460 - 16, py = 680 - U.popupHeight() - 16;
      const wx = 380, wy = 214;
      const stage = OB.h(`<div class="sw-stage"><div class="sw-cam">${U.desktop()}
        ${U.popup({ x: px, y: py, name: "PhotoDrop.exe", path: U.P.photodrop, armed: true,
          status: "SecureWall is locked. Enter the password to allow this app, or close this prompt to keep it blocked." })}
        ${U.menu({ id: "tray-menu", x: 1076 - 249, bottom: 680, w: 249, items: U.trayItems() })}
        ${U.password({ x: wx, y: wy })}
      </div></div>`);
      root.appendChild(stage);
      const $ = (s) => stage.querySelector(s);
      const camEl = $(".sw-cam"), menu = $('[data-sw="tray-menu"]'), lock = $('[data-sw="mi-lock"]');
      const trayIcon = $('[data-sw="tray-shield"]'), pop = $('[data-sw="popup"]'), allow = $('[data-sw="pop-allow"]');
      const pw = $('[data-sw="password"]'), dots = $('[data-sw="pw-dots"]'), status = $('[data-sw="pop-status"]');
      const cam = U.cam(1.4, 850, 460);
      camEl.style.transform = cam.css;

      const S = 34, aim = (x, y) => { const m = cam.map(x, y); return ctx.aim(m.x, m.y, S); };
      const tr = U.local(trayIcon), lk = U.local(lock), al = U.local(allow);
      const pTray = aim(tr.cx, tr.cy), pLock = aim(lk.x + 50, lk.cy), pAllow = aim(al.x + al.w - 12, al.cy + 8);
      ctx.actor(U.shiftKeys(T.trim, [
        { t: 0, x: 560, y: 260, size: S },
        { t: T.move, x: 560, y: 260 },
        { t: T.tapTray - 220, x: pTray.x, y: pTray.y, ease: "out" },
        { t: T.tapTray, tap: true }, { t: T.tapTray + 180, tap: false },
        { t: T.onLock, x: pLock.x, y: pLock.y, ease: "inOut" },
        { t: T.tapLock, tap: true }, { t: T.tapLock + 180, tap: false },
        { t: T.show, x: pLock.x - 140, y: pLock.y - 120, ease: "inOut" },
        { t: T.tapAllow - 250, x: pAllow.x, y: pAllow.y, ease: "out" },
        { t: T.tapAllow, tap: true }, { t: T.tapAllow + 180, tap: false },
        { t: T.type, x: pAllow.x + 30, y: pAllow.y + 40, ease: "inOut" },
        { t: T.dur, x: pAllow.x + 34, y: pAllow.y + 42 },
      ]));

      const set = OB.writer();
      return {
        frame(t) {
          t += T.trim; // the opening that was cut
          set(menu, "visibility", t >= T.open && t < T.tapLock + 120 ? "visible" : "hidden");
          set(lock, "class", "sw-mi" + (t >= T.onLock - 120 ? " sw-hover" : ""));
          set(trayIcon, "class", "sw-tray-icon" + (t >= T.tapTray - 220 && t < T.tapLock + 120 ? " sw-hot" : ""));
          U.openFx(set, pop, t, T.show);
          const hover = t >= T.tapAllow - 250, press = t >= T.tapAllow && t < T.tapAllow + 180;
          // Disabled only while the request is in flight; the locked answer enables it again.
          const armed = t >= T.arm && !(t >= T.tapAllow + 180 && t < T.pw);
          set(allow, "class", "sw-pop-btn sw-pop-allow" + (armed ? "" : " sw-disabled") + (press ? " sw-press" : hover && armed ? " sw-hover" : ""));
          set(status, "visibility", t >= T.pw ? "visible" : "hidden");
          U.openFx(set, pw, t, T.pw);
          set(dots, "text", "●".repeat(Math.min(10, Math.max(0, Math.floor((t - T.type) / 1000 * 7)))));
        },
        destroy() {},
      };
    },
  });
})();
