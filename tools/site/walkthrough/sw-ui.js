// SecureWall UI builders shared by the chapters. Each returns markup that securewall-ui.css
// renders. Positions are the product's own: client coordinates from the Designer.cs and
// .resx files (or, for windows laid out in code, measured from the /uipreview captures).
// Strings are the product's English strings; sources are cited next to each builder.
(function () {
  const A = (n) => "assets/" + n + ".png";
  const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");

  // Demo cast; paths and addresses match TinyWall/UiPreviewSamples.cs.
  const P = {
    inkwell: "C:\\Program Files\\Inkwell\\Inkwell.exe",
    telemetry: "C:\\Users\\Alex\\AppData\\Roaming\\Updater\\telemetry_agent.exe",
    notesync: "C:\\Users\\Alex\\AppData\\Local\\NoteSync\\app-3.2.0\\NoteSync.exe",
    photodrop: "C:\\Program Files\\PhotoDrop\\PhotoDrop.exe",
    svchost: "C:\\Windows\\System32\\svchost.exe",
  };

  // ── desktop with taskbar; tray: icon colour of the SecureWall shield
  function desktop(o = {}) {
    const shield = { normal: "shield_green_small", blockall: "shield_yellow_small", allowout: "shield_red_small", disabled: "shield_grey_small" }[o.mode || "normal"];
    return `<div class="sw-desktop"></div>
      <div class="sw-taskbar">
        <div class="sw-start"><i><b></b><b></b><b></b><b></b></i></div>
        <div class="sw-search">Type here to search</div>
        <div class="sw-tray">
          <div class="sw-tray-icon" data-sw="tray-shield"><img data-sw="tray-shield-img" src="${A(shield + "_16")}" alt=""></div>
          <div class="sw-tray-icon"><span class="sw-tray-glyph"></span></div>
          <div class="sw-clock">10:24 AM<br>11/1/2026</div>
          <div class="sw-shownotif"></div>
        </div>
      </div>`;
  }
  const shieldSrc = (mode) => A({ normal: "shield_green_small", blockall: "shield_yellow_small", allowout: "shield_red_small", disabled: "shield_grey_small" }[mode] + "_16");

  // ── window frame. w/h = client size; the frame adds 2 px and 32 px.
  function frame(o) {
    const caps = o.caps === "none" ? "" : o.caps === "close"
      ? '<div class="sw-cap sw-close"></div>'
      : '<div class="sw-cap sw-min"></div><div class="sw-cap sw-max"></div><div class="sw-cap sw-close"></div>';
    return `<div class="sw-win ${o.themed ? "sw-themed" : ""} ${o.cls || ""}" data-sw="${o.id || ""}" style="left:${o.x}px;top:${o.y}px;width:${o.w + 2}px;height:${o.h + 32}px">
      <div class="sw-title">${o.icon === false ? "" : `<img class="sw-title-icon" src="${A("firewall_16")}" alt="">`}<div class="sw-title-text" style="${o.icon === false ? "left:8px" : ""}">${esc(o.title)}</div><div class="sw-caps">${caps}</div></div>
      <div class="sw-client">${o.body}</div></div>`;
  }
  const btn = (x, y, w, h, text, o = {}) =>
    `<div class="sw-btn ${o.cls || ""}" data-sw="${o.id || ""}" style="left:${x}px;top:${y}px;width:${w}px;height:${h}px;${o.style || ""}">${o.icon ? `<img src="${A(o.icon)}" alt="">` : ""}<span>${esc(text)}</span></div>`;
  const check = (x, y, text) => `<div class="sw-check" style="left:${x}px;top:${y}px;height:17px"><i></i><span>${esc(text)}</span></div>`;

  // ── list view (details, grid lines). cols: [[title, width]], rows: {id, icon, cells[], sel, missing}
  function list(o) {
    const head = o.cols.map(([t, w]) => `<div style="width:${w}px">${esc(t)}</div>`).join("");
    const rows = o.rows.map((r) => row(o.cols, r)).join("");
    let edge = 0;
    const lines = o.cols.map(([, w]) => `<div class="sw-lcol" style="left:${(edge += w) - 1}px"></div>`).join("");
    return `<div class="sw-list" data-sw="${o.id || ""}" style="left:${o.x}px;top:${o.y}px;width:${o.w}px;height:${o.h}px">
      <div class="sw-lfill"></div>${lines}
      <div class="sw-lhead">${head}</div>
      <div class="sw-lrows" data-sw="${o.id ? o.id + "-rows" : ""}">${rows}</div>
      ${o.hscroll ? `<div class="sw-hscroll" style="--thumb:${o.hscroll}"><i class="sw-l"></i><i class="sw-r"></i></div>` : ""}</div>`;
  }
  function row(cols, r) {
    const cells = r.cells.map((c, i) => i === 0 && !r.noicon
      ? `<div style="width:${cols[0][1]}px"><img src="${A(r.missing ? "delete" : r.icon || "exe_generic")}" alt=""><span>${esc(c)}</span></div>`
      : `<div style="width:${cols[i][1]}px">${esc(c)}</div>`).join("");
    return `<div class="sw-lrow ${r.sel ? "sw-sel" : ""} ${r.missing ? "sw-missing" : ""}" data-sw="${r.id || ""}">${cells}</div>`;
  }

  // ── Network Activity (ConnectionsForm.Designer.cs / .resx). Client 1016 x 486.
  const NA_COLS = [["Process (id)", 120], ["Service", 120], ["Protocol", 75], ["Local port", 68], ["Local address", 160],
    ["Remote port", 92], ["Remote address", 160], ["State", 88], ["Direction", 60], ["Timestamp", 146]];
  // Sorted newest first, as the window sorts by Timestamp (ListViewItemComparer(9, null, false)).
  const NA_ROWS = [
    { id: "na-inkwell-live", cells: ["Inkwell.exe (7408)", "", "TCP", "51544", "192.168.1.20", "  443", "203.0.113.45 (sync.inkwell.example)", "Established", "", "2026/11/01 10:24:30"] },
    { id: "na-system", icon: "windows_small", cells: ["System (4)", "", "TCP", "  445", "192.168.1.20", "    0", "0.0.0.0", "Listening (local endpoint)", "", "2026/11/01 10:24:30"] },
    { id: "na-photodrop", cells: ["PhotoDrop.exe (8816)", "", "TCP", "51610", "192.168.1.20", "  443", "198.51.100.23 (upload.photodrop.example)", "Blocked (default deny)", "Out", "2026/11/01 10:24:28"] },
    { id: "na-inkwell", cells: ["Inkwell.exe (7408)", "", "TCP", "51598", "192.168.1.20", "  443", "203.0.113.45 (sync.inkwell.example)", "Allowed", "Out", "2026/11/01 10:24:26"] },
    { id: "na-dns", cells: ["svchost.exe (1432)", "Dnscache", "UDP", "55012", "192.168.1.20", "   53", "1.1.1.1", "Allowed", "Out", "2026/11/01 10:24:24"] },
    { id: "na-telemetry", cells: ["telemetry_agent.exe", "", "TCP", "51633", "192.168.1.20", "  443", "198.51.100.7", "Blocked (ignored)", "Out", "2026/11/01 10:24:21"] },
    { id: "na-notesync", cells: ["NoteSync.exe (5236)", "", "TCP", "51587", "192.168.1.20", "  443", "192.0.2.80 (api.notesync.example)", "Blocked, then allowed by Always allow", "Out", "2026/11/01 10:24:09"] },
  ];
  function networkActivity(o = {}) {
    const rows = (o.rows || NA_ROWS).map((r) => Object.assign({}, r, { sel: r.id === o.selected }));
    const body = list({ id: "na-list", x: 12, y: 12, w: 992, h: 390, cols: NA_COLS, rows, hscroll: "86%" })
      + check(15, 408, "Show listening endpoints") + check(15, 431, "Show allowed + active") + check(15, 454, "Show blocked (last 5 minutes)")
      + btn(818, 441, 90, 33, "Refresh") + btn(914, 441, 90, 33, "Close");
    return frame({ id: o.id || "na", x: o.x, y: o.y, w: 1016, h: 486, title: "SecureWall Network Activity", themed: true, body });
  }

  // ── Manage (SettingsForm). Client 748 x 438; tab control (12,12) 724 x 375; page (4,22) 716 x 349.
  const TABS = ["General", "Application Exceptions", "Special Exceptions", "Maintenance", "Always allow", "Ignored"];
  const EX_COLS = [["Application", 132], ["Type", 102], ["Details", 356]];
  const EX_ROWS = [
    { id: "ex-dns", icon: "exe_generic", cells: ["Dnscache", "Service", P.svchost] },
    { id: "ex-inkwell", cells: ["Inkwell.exe", "Executable", P.inkwell] },
    { id: "ex-notesync", cells: ["NoteSync.exe", "Executable", P.notesync] },
  ];
  function manage(o = {}) {
    const tab = o.tab || "Application Exceptions";
    const strip = TABS.map((t) => `<div class="sw-tab ${t === tab ? "sw-on" : ""}">${esc(t)}</div>`).join("");
    let page = "";
    if (tab === "Application Exceptions") page = exceptionsPage(o);
    else if (tab === "Always allow") page = dockedListPage(o, "These apps are allowed outgoing connections from any folder, as long as the file has this name and a valid signature from this publisher. SecureWall adds an exact rule the first time each new copy is blocked; its first connection can fail once.",
      [["File name", 180], ["Publisher", 320]], o.rows || []);
    else if (tab === "Ignored") page = dockedListPage(o, "You chose Ignore for these apps. Their blocked connections stay blocked, and SecureWall no longer asks about them. Remove an app to be asked again the next time it is blocked.",
      [["Name", 180], ["Path or package", 320]], o.rows || []);
    const body = `<div class="sw-tabs" style="left:12px;top:12px;width:724px;height:375px"><div class="sw-tabstrip">${strip}</div>
        <div class="sw-tabpage">${page}</div></div>`
      + btn(526, 393, 100, 33, "Apply", { icon: "accept" }) + btn(632, 393, 100, 33, "Cancel", { icon: "cancel" });
    return frame({ id: o.id || "manage", x: o.x, y: o.y, w: 748, h: 438, title: "SecureWall Firewall Settings", themed: true, caps: "close", body });
  }
  // Page coordinates are the .resx values minus the page border (1 px).
  function exceptionsPage(o) {
    const rows = o.rows || EX_ROWS;
    const filter = o.filter || "";
    return `<div class="sw-label" style="left:22px;top:30px;width:520px">Edit the list of applications that you'd like to grant network access. Only applications in this list will be able to communicate. Use the Detect button to let SecureWall detect some software, or add applications manually.</div>
      <div class="sw-label" style="left:22px;top:78px">Filter:</div>
      <div class="sw-text ${o.filterFocus ? "sw-focus" : ""}" data-sw="filter" style="left:60px;top:74px;width:480px"><span data-sw="filter-text">${esc(filter)}</span>${o.caret ? '<span class="sw-caret" data-sw="caret"></span>' : ""}</div>
      ${list({ id: "ex-list", x: 25, y: 96, w: 515, h: 229, cols: EX_COLS, rows, hscroll: "71%" })}
      ${btn(565, 29, 127, 36, "Detect", { icon: "uninstall", cls: "sw-flat", style: "font-weight:bold;justify-content:flex-start;padding-left:8px;gap:30px" })}
      ${btn(565, 96, 127, 36, "Add application", { icon: "add", cls: "sw-flat", style: "justify-content:flex-start;padding-left:8px" })}
      ${btn(565, 138, 127, 36, "Modify", { icon: "modify", cls: "sw-flat sw-disabled", style: "justify-content:flex-start;padding-left:8px;gap:24px" })}
      ${btn(565, 180, 127, 36, "Remove", { icon: "remove", cls: "sw-flat sw-disabled", style: "justify-content:flex-start;padding-left:8px;gap:24px" })}
      ${btn(565, 222, 127, 36, "Remove all", { icon: "remove", cls: "sw-flat", style: "justify-content:flex-start;padding-left:8px;gap:12px" })}`;
  }
  // The Always allow and Ignored tabs: help label (3 lines), list, Remove button (SettingsForm.AlwaysAllow.cs, .Ignored.cs).
  function dockedListPage(o, help, cols, rows) {
    return `<div class="sw-label" style="left:5px;top:5px;width:704px;line-height:13px">${esc(help)}</div>
      ${list({ id: o.listId || "tab-list", x: 5, y: 48, w: 704, h: 264, cols, rows })}
      ${btn(632, 316, 75, 24, "Remove", { icon: "remove", cls: "sw-disabled", id: "tab-remove" })}`;
  }

  // ── blocked-connection popup (BlockedConnectionPopup*.cs). 460 x (270 + warnings).
  const WARN = { unsigned: "Not signed by a trusted publisher", writable: "Located in a folder your user account can modify", recent: "File changed within the last 24 hours" };
  function popup(o) {
    const warns = (o.warnings || []).map((k) => "\u26a0 " + WARN[k]);
    const extra = warns.length ? 4 + 15 * warns.length : 0; // RiskLabelGap + one 15 px line each
    const by = 222 + extra;
    const allowCls = o.armed ? "" : "sw-disabled";
    return `<div class="sw-popup" data-sw="${o.id || "popup"}" style="left:${o.x}px;top:${o.y}px;height:${270 + extra}px">
      <div class="sw-pop-title"><b>SecureWall blocked an outgoing connection</b><div class="sw-pop-close">\u00d7</div></div>
      <div class="sw-pop-ai">?</div>
      <div class="sw-pop-id">${esc(o.name)}</div>
      <div class="sw-pop-path">${esc(o.path)}</div>
      <div class="sw-pop-dest">Destination: ${esc(o.dest || "TCP  203.0.113.20:443")}</div>
      <div class="sw-pop-notice">Allow permanently permits this app, package, or service to reach all destinations and ports over TCP/UDP.</div>
      ${warns.length ? `<div class="sw-pop-warn">${warns.map(esc).join("<br>")}</div>` : ""}
      ${o.alwaysAllow ? `<div class="sw-pop-btn sw-pop-always ${allowCls}" data-sw="pop-always" style="left:102px;top:${by}px">Always allow</div>` : ""}
      <div class="sw-pop-btn sw-pop-allow ${allowCls}" data-sw="pop-allow" style="left:226px;top:${by}px">Allow outgoing</div>
      <div class="sw-pop-btn sw-pop-ignore" data-sw="pop-ignore" style="left:366px;top:${by}px">Ignore</div>
    </div>`;
  }
  const popupHeight = (warnings) => 270 + ((warnings || []).length ? 4 + 15 * warnings.length : 0);

  // ── menus. items: {icon, text, sub, sep, hover, dis, id}
  function menu(o) {
    const items = o.items.map((it) => it.sep ? '<div class="sw-msep"></div>'
      : `<div class="sw-mi ${it.sub ? "sw-sub" : ""} ${it.hover ? "sw-hover" : ""} ${it.dis ? "sw-dis" : ""}" data-sw="${it.id || ""}">${it.icon ? `<img src="${A(it.icon)}" alt="">` : ""}${esc(it.text)}</div>`).join("");
    return `<div class="sw-menu ${o.themed ? "sw-themed" : ""}" data-sw="${o.id || ""}" style="left:${o.x}px;${o.bottom != null ? `bottom:${720 - o.bottom}px` : `top:${o.y}px`};width:${o.w}px">${items}</div>`;
  }
  // Tray menu (TinyWallController.cs InitializeComponent, MainForm.resx strings; ref tray.png).
  function trayItems(o = {}) {
    const modeIcon = { normal: "shield_green_small_16", blockall: "shield_yellow_small_16", allowout: "shield_red_small_16", disabled: "shield_grey_small_16" }[o.mode || "normal"];
    const items = [
      { icon: "info", text: "In: 12.4 KiB/s   Out: 3.1 KiB/s" }, { sep: true },
      { icon: modeIcon, text: "Change mode", sub: true, id: "mi-mode" },
      { icon: "manage", text: "Manage", id: "mi-manage" },
      { icon: "connections", text: "Network activity", id: "mi-na" },
    ];
    if (!o.locked) items.push({ icon: "lock_small", text: "Lock", id: "mi-lock" });
    items.push({ icon: "uac_shield", text: "Elevate" }, { sep: true },
      { text: "Unblock LAN traffic" }, { text: "Enable blocklists" }, { sep: true },
      { icon: "executable", text: "Whitelist by executable" }, { icon: "process", text: "Whitelist by process" }, { icon: "window", text: "Whitelist by window" }, { sep: true },
      { text: "Check for updates...", id: "mi-update" }, { icon: "exit", text: "Quit" });
    for (const it of items) if (it.id && it.id === o.hover) it.hover = true;
    return items;
  }
  const MODE_ITEMS = (hover) => [
    { icon: "shield_green_small_16", text: "Normal protection", id: "mm-normal" },
    { icon: "shield_yellow_small_16", text: "Block all", id: "mm-blockall" },
    { icon: "shield_red_small_16", text: "Allow outgoing", id: "mm-allowout" },
    { icon: "shield_grey_small_16", text: "Disable firewall", id: "mm-disabled" },
  ].map((it) => Object.assign(it, { hover: it.id === hover }));
  // Row menu of Network Activity (ConnectionsForm.Designer.cs; ref na-*-menu.png).
  const NA_MENU = (hover) => [
    { icon: "executable", text: "Unblock", id: "nm-unblock" }, { icon: "exit", text: "Kill process" },
    { icon: "search", text: "Search...", sub: true }, { icon: "copy", text: "Copy remote address" },
    { icon: "copy", text: "Copy path" }, { icon: "open_folder", text: "Open folder" },
  ].map((it) => Object.assign(it, { hover: it.id === hover }));

  // ── password dialog (PasswordForm.resx). Client 422 x 127; follows the theme.
  function password(o) {
    const dots = "\u25cf".repeat(o.dots || 0);
    const body = `<div class="sw-label" style="left:9px;top:9px;width:292px;line-height:14px;letter-spacing:-0.4px">The requested firewall operation requires additional privileges. Please enter your passphrase to authenticate.</div>
      <div class="sw-text sw-focus" style="left:12px;top:52px;width:289px"><span data-sw="pw-dots" style="letter-spacing:1px">${dots}</span><span class="sw-caret"></span></div>
      ${btn(95, 82, 100, 33, "OK", { icon: "accept", cls: "sw-default" })}${btn(201, 82, 100, 33, "Cancel", { icon: "cancel" })}
      <img src="${A("lock")}" alt="" style="position:absolute;left:314px;top:19px;width:96px;height:96px">`;
    return frame({ id: o.id || "password", x: o.x, y: o.y, w: 422, h: 127, title: "Password - SecureWall", themed: true, caps: "none", icon: false, body });
  }

  // ── update window (UpdateDialog.cs, laid out in code; positions measured from update.png). Light always.
  function update(o) {
    const seg = "font-family:'Segoe UI',system-ui,sans-serif;font-size:12px;letter-spacing:0.2px";
    let body;
    if (o.state === "downloading") {
      body = `<div style="position:absolute;left:12px;top:12px;font-weight:bold;font-size:15.6px;${seg};font-size:15.6px">Downloading SecureWall 0.5.2</div>
        <div style="position:absolute;left:12px;top:42px;width:576px;${seg}">The installer is checked against the release's SHA256SUMS.txt before anything is installed.</div>
        <div class="sw-progress" style="left:12px;top:400px;width:576px"><i data-sw="progress" style="width:${Math.round(576 * (o.progress || 0))}px"></i></div>
        ${btn(511, 430, 75, 23, "Cancel", { style: seg })}`;
    } else {
      body = `<div style="position:absolute;left:12px;top:12px;font-weight:bold;${seg};font-size:15.6px">SecureWall 0.5.2 is available</div>
        <div style="position:absolute;left:12px;top:42px;width:576px;line-height:16px;${seg}">Published 11/1/2026 as a prerelease. You have 0.5.1. The installer is checked against the release's SHA256SUMS.txt after download.</div>
        <div style="position:absolute;left:12px;top:94px;${seg}">Release notes from GitHub:</div>
        <div class="sw-text" style="left:12px;top:116px;width:576px;height:236px;white-space:pre-wrap;line-height:15px;padding:3px 22px 3px 4px;background:#f0f0f0;border-color:#adadad;${seg}"><div class="sw-vscroll"></div>This is an unsigned alpha prerelease. SecureWall can now check GitHub for a newer release and install it, and the Network Activity list no longer moves while it refreshes.

## Changes

### Check for updates

- **Where.** **Check for updates** is in the tray menu and on the Maintenance tab in Manage. It runs only when you choose it; SecureWall never checks on its own. If SecureWall is locked, it asks for the password first.</div>
        <div style="position:absolute;left:12px;top:356px;width:576px;line-height:16px;${seg}">Installing stops SecureWall and blocks all connections to other computers until the new version starts, usually under a minute. Open connections drop. Windows asks for administrator permission, then setup shows its own window.</div>
        ${btn(322, 430, 75, 23, "Install", { id: "up-install", cls: "sw-default " + (o.installCls || ""), style: seg })}
        ${btn(404, 430, 99, 23, "Skip this version", { style: seg })}
        ${btn(511, 430, 75, 23, "Later", { style: seg })}`;
    }
    return frame({ id: o.id || "update", x: o.x, y: o.y, w: 600, h: 470, title: "SecureWall update", themed: false, caps: "close", body });
  }

  // ── Windows notification for a NotifyIcon balloon (title is the product name).
  const toast = (o) => `<div class="sw-toast" data-sw="${o.id || "toast"}" style="left:${o.x}px;top:${o.y}px"><img src="${A("info")}" alt=""><b>SecureWall</b><p>${esc(o.text)}</p><small>SecureWall</small></div>`;
  const tip = (o) => `<div class="sw-tip" data-sw="${o.id || "tip"}" style="left:${o.x}px;top:${o.y}px;width:${o.w || 300}px">${esc(o.text)}</div>`;

  // ── camera: the desktop is laid out at 1:1 on a 1200 x 720 screen, and the stage shows it
  // magnified so 96-dpi text stays readable in a small embed. cam(z, fx, fy) puts desktop point
  // (fx, fy) at the stage centre, clamped so the screen edges never come into view.
  function cam(z, fx, fy) {
    const tx = Math.min(0, Math.max(1200 - 1200 * z, 600 - fx * z));
    const ty = Math.min(0, Math.max(720 - 720 * z, 360 - fy * z));
    return { z, tx, ty, css: `translate(${tx.toFixed(2)}px,${ty.toFixed(2)}px) scale(${z.toFixed(4)})`,
      map: (x, y) => ({ x: tx + x * z, y: ty + y * z }) };
  }
  // Blend two cameras (u: 0..1).
  function camMix(a, b, u) {
    const z = a.z + (b.z - a.z) * u, tx = a.tx + (b.tx - a.tx) * u, ty = a.ty + (b.ty - a.ty) * u;
    return { z, tx, ty, css: `translate(${tx.toFixed(2)}px,${ty.toFixed(2)}px) scale(${z.toFixed(4)})`, map: (x, y) => ({ x: tx + x * z, y: ty + y * z }) };
  }

  // Desktop coordinates of an element inside the camera layer (offsets ignore the camera's transform).
  function local(el) {
    let x = 0, y = 0, n = el;
    while (n && !n.classList.contains("sw-cam")) { x += n.offsetLeft; y += n.offsetTop; n = n.offsetParent; }
    return { x, y, w: el.offsetWidth, h: el.offsetHeight, cx: x + el.offsetWidth / 2, cy: y + el.offsetHeight / 2 };
  }
  // A window opening the way Windows 10 animates it: a quick fade with a slight grow.
  function openFx(set, el, t, start, end) {
    const vis = t >= start && (end == null || t < end);
    const u = Math.min(1, Math.max(0, (t - start) / 160)), e = 1 - Math.pow(1 - u, 3);
    set(el, "visibility", vis ? "visible" : "hidden");
    set(el, "opacity", vis ? e.toFixed(3) : "0");
    set(el, "transform", vis && u < 1 ? `scale(${(0.96 + 0.04 * e).toFixed(4)})` : "none");
  }

  window.SWUI = { local, openFx, cam, camMix, P, desktop, shieldSrc, frame, btn, check, list, row, networkActivity, NA_COLS, NA_ROWS, manage, EX_COLS, EX_ROWS,
    popup, popupHeight, menu, trayItems, MODE_ITEMS, NA_MENU, password, update, toast, tip, esc };
})();
