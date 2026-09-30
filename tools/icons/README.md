# Icon render tools

These scripts turn the SVG masters in `TinyWall/Resources/img/svg/` into the PNG, ICO and BMP files the application and installer use. Edit an SVG, render, validate, then build.

Requirements: Node.js (tested with Node 24) and Google Chrome (used headless to rasterize). No npm packages.

```powershell
node tools\icons\render.mjs
node tools\icons\validate.mjs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\icons\contact.ps1
```

- `jobs.mjs` lists every output: file path, source SVG, format and pixel sizes. Add or remove an asset here.
- `render.mjs` rasterizes each SVG at each listed size in one headless Chrome run (device scale 1, window off screen) and writes the files over their repository paths. Options: `--out <dir>` renders into a staging folder instead, `--svg <dir>` reads other masters, `--manifest <file>` writes a Markdown table of the outputs, `--chrome <path>` points at a Chrome that is not in the default install location. Output is byte-identical across runs on the same Chrome version.
- `pack.mjs` writes the ICO (32-bit DIB entries with an AND mask; the 256 px entry is PNG-compressed) and 24-bit BMP containers.
- `validate.mjs` checks each output's container structure and sizes against `jobs.mjs` and exits 1 on any mismatch. `--out <dir>` validates a staging folder.
- `contact.ps1` loads every output through .NET Framework `System.Drawing`, the loader the application uses, and saves a contact sheet (default `.tmp\icons-contact.png`). Run it with Windows PowerShell 5.1, not `pwsh`.

Sizes: the app icon (`firewall.ico`, `SecureWall.ico`) carries 16 to 256 px; tray shields carry 16, 20, 24, 32, 40 and 48 px so the tray and menus get an exact entry at 100, 125 and 150% scaling; buttons and list icons are 16 px PNGs that the application scales with the display.
