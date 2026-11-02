// SecureWall product walkthrough. The player (index.html) reads window.TOUR before it boots.
window.TOUR = {
  title: "SecureWall tour",

  // SecureWall is a WinForms app: securewall-ui.css rebuilds its windows (see its header).
  css: ["securewall-ui.css"],
  // Shared window builders the chapters use, loaded before them.
  lib: ["sw-ui.js"],

  bodyClass: "",
  rootClass: "sw-stage",

  stage: { w: 1200, h: 720 },

  // The Windows app theme. Network Activity, Manage and the password dialog follow it;
  // the popup, tray menu and update window stay light, as in the product.
  themes: { dark: { class: "dark", ink: "#f1f1f4", page: "#0d1117" }, light: { ink: "#16161a", page: "#eef1f5" } },
  defaultTheme: "dark",

  chapters: ["ch1-allow", "ch2-ignore", "ch3-always-allow", "ch4-activity", "ch5-rules", "ch6-modes", "ch7-lock", "ch8-update"],

  // A Windows-sized arrow pointer.
  actor: { src: "actors/cursor.js", id: "cursor", size: 34 },

  // No hold between chapters: each one ends as its result lands (owner review, 2026-11-01).
  gapMs: 0,
  maxScale: 1.25,

  // The popup's Allow blue (BlockedConnectionPopup.Designer.cs).
  accent: "#2563eb",

  // The host page that embeds the player (embed mode only).
  parentOrigin: "https://secwall.org",

  reducedMotion: "end-frame",

  strings: { chapter: "Chapter {n} of {total}", back: "Back", next: "Next", finish: "Finish", skip: "Skip tour", cta: "Try it",
    pause: "Pause", play: "Play", chapters: "Chapters" },
};
