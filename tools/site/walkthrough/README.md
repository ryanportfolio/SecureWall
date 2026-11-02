# SecureWall walkthrough

An animated tour of the SecureWall desktop app for secwall.org: eight short chapters that replay the app's windows while a pointer performs one action in each. It is a static page with no build step and no dependencies.

```powershell
node tools\site\serve-site.mjs tools\site\walkthrough 4318
```

Then open `http://127.0.0.1:4318/`, or `film.html` to play the whole tour on one timeline and mark stretches to cut (I and O; Copy cuts gives chapter-local times). Add `?theme=light` for the light app theme, `?scene=ch4-activity` to open a chapter, `?t=3300` to freeze a moment, `?dev=0` to hide the dev bar. Space pauses, the arrow keys change chapter.

| # | Chapter | Shows |
|---|---|---|
| 1 | `ch1-allow` | The blocked-connection popup; Allow outgoing enables after 1 s and is clicked |
| 2 | `ch2-ignore` | Ignore on an unsigned app, then Manage, Ignored |
| 3 | `ch3-always-allow` | Always allow on a signed app, its tooltip, then Manage, Always allow |
| 4 | `ch4-activity` | Network Activity, right-click Unblock, the Windows notification |
| 5 | `ch5-rules` | Manage, Application Exceptions, typing in Filter |
| 6 | `ch6-modes` | Tray menu, Change mode, Block all, yellow tray icon |
| 7 | `ch7-lock` | Tray Lock, then Allow on a popup asks for the password |
| 8 | `ch8-update` | Tray Check for updates, the update window, Install, download progress |

The tour runs about 44 seconds. Each chapter ends as its result appears and the next starts at once (`gapMs: 0`); `T.trim` in a scene skips the start of its beat sheet. These cuts came from the owner's review with `film.html`.

## How the windows are rebuilt

SecureWall is a WinForms app, so there is no stylesheet to copy. `securewall-ui.css` and `sw-ui.js` rebuild each window at its real 96-dpi size from the `*.Designer.cs` and `.resx` geometry, with the product's English strings and its icons from `TinyWall/Resources/img`. Network Activity, Manage and the password dialog follow the app theme; the popup, tray menu and update window stay light, as they do in the product. The scenes magnify the screen (`SWUI.cam`) so the text stays readable in a small embed.

The demo apps (Inkwell, telemetry_agent, NoteSync, PhotoDrop) are made up and use documentation IP ranges. `TinyWall/UiPreviewSamples.cs` holds the same data, so the replicas can be compared with the real windows.

## Checking it against the app

The Debug build opens each window with the sample data, without the service, its pipe or WFP, and can save it as a PNG:

```powershell
$exe = 'TinyWall\bin\Debug\SecureWall.exe'
$db = 'MsiSetup\Sources\CommonAppData\SecureWall\profiles.json'
Start-Process $exe -ArgumentList "/uipreview connections /menu /dark /database $db /capture D:\screenshots\SecureWall\walkthrough\refs\na-dark.png" -Wait
Start-Process $exe -ArgumentList '/promptpreview /executable "C:\Users\Alex\AppData\Local\NoteSync\app-3.2.0\NoteSync.exe" /warnings writable /signer "NoteSync Labs Inc." /capture D:\screenshots\SecureWall\walkthrough\refs\prompt-notesync.png' -Wait
```

Screens: `tray` (`/mode`, `/password`, `/submenu`), `connections` (`/menu`), `manage` (`/tab <index>`), `update` (`/downloading`), `password`. `UiPreview.cs` lists every switch. Re-capture and compare after any change to a window the tour shows; a chapter whose window no longer matches is updated or removed from `tour.config.js` until it is.

The review scripts (contact sheets, determinism, tap spot checks, ship check) come from the `product-walkthrough` skill's `templates/checks` and are not kept in the repository.

## Embedding

`index.html?embed=1` runs the postMessage protocol for a host page (`ready`, `start`, `theme`, `skip`, `tour-done`). `tour.config.js` accepts messages only from `https://secwall.org`; serve the page with a `frame-ancestors` policy that admits only the site (the patcher sets `'self'`). Chapter buttons link to `/#team` (downloads) and `/#public-goods` (guides).

## On secwall.org

The site's Tour section (`tools/site/sections/tour.js` and `tour.css`) sits between the download cards and "Before you install". It frames `/walkthrough/index.html?capture=1&fill=1&dev=0` from the same origin and drives it with `__player.set()`, as `film.html` does: one 44-second timeline, a chapter log that also seeks, pause, a scrubber, and a Dark/Light switch for the app theme. It plays while in view, loops, and with `prefers-reduced-motion` waits for Play. `fill=1` makes the stage fill the frame with no page colour, rounding or shadow, and stops the player's own animation loop: every frame comes from the section's `__player.set()` calls. `tools/site/patch-secwall-site.mjs` copies this folder (without this README and `film.html`) to `/walkthrough/` and adds `Content-Security-Policy: frame-ancestors 'self'` and `X-Robots-Tag: noindex` for it in `vercel.json`. Not on production yet.
