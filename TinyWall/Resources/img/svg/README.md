# SecureWall icon sources

The SVG files in this directory are original works drawn for SecureWall from scratch. They contain no traced, copied, or font-derived artwork and no third-party assets. They are distributed under the project's license, the GNU General Public License v3 (see `LICENSE` at the repository root).

These files are the source of truth for every image in `TinyWall/Resources/img/` and for the installer's `MsiSetup/banner.bmp`, `MsiSetup/background.bmp` and `SecureWall.ico`. Regenerate the rendered files with the scripts in `tools/icons/` (see its README); do not edit the PNG, ICO or BMP files by hand.

- Icons use a 16 x 16 grid with 1 px edges on half-pixel lines so they stay sharp at 16 px; the same file is rendered at every other size.
- Most file names match the resource name in `TinyWall/Resources/Icons.resx`. The exceptions: `firewall.svg` renders `firewall.ico` and the installer's `SecureWall.ico`; `shield_<color>.svg` renders `shield_<color>_small.ico`; `lock.svg` renders both `lock.png` (96 px) and `lock_small.png` (16 px); `installer_banner.svg` and `installer_dialog.svg` render `MsiSetup/banner.bmp` and `MsiSetup/background.bmp`. `tools/icons/jobs.mjs` holds the full mapping.
- `shield_unknown` is the tray icon while the service cannot be reached. `windows_small` is a plain monitor for Windows system entries in lists, not the Windows logo.
- `preview.html` shows every icon at 16, 24, 32 and 48 px on light and dark backgrounds. Open it in a browser from this directory.
