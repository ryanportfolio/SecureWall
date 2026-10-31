# secwall.org headline redesign

Status: plan, nothing built. Written 2026-10-31 from a `/redesign-concepts` run.

## Goal

The three big headlines on https://secwall.org sit flat on top of the 3D terrain background. The terrain has depth, light and rainbow edges; the text has none of that, and it does not say anything about blocking or allowing connections. The owner picked three concepts that make the headlines part of the terrain:

| Section | Headline | Picked concept |
|---|---|---|
| Hero | Choose what connects | 01 terrain type: the letters rise out of the terrain as stacked layers with rainbow edges, lit and shaded like the rest of the surface |
| Block section | Block by default | 05 wall: "Block" rises as a dark wall the terrain piles up against; "by default" is set into the ground in front |
| Control section | Full Control | 09 app chips: white letters rise from the terrain, with small cards around them at different depths naming apps and their Allow or Block rule |

Concept images and the live captures they were made from: `D:\CoreWise\_artifacts\SecureWall\redesign-1\` (`01-hero-terrain-type.png`, `05-block-wall-of-type.png`, `09-control-app-chips.png`, plus `hero.png`, `block.png`, `control.png`). They are design targets, not specs: text, fonts and colors in them are corrected below.

Problems in the live site that this fixes:

- The headlines are DOM text with a heavy black `text-shadow` over the canvas and never interact with the terrain.
- "Block by default" is black and "Full Control" is white; the color change carries no meaning.
- The only transition is a word-by-word slide and fade.
- On a 1440x900 viewport the hero tagline overlaps the word "connects" and hides part of "A default-deny".

## How the live site works

The site has no source in any repository. `deployment.md` (section "secwall.org") covers hosting. What the prebuilt bundle (`/build/index-UaCeo62-.js`, 1.29 MB) shows:

- The headlines are plain DOM: the hero is an `h1.js-hero:title`; the two section titles are `h2.js-webgl-title-text` elements with the classes `sw-block-title` and `sw-control-title`, inside fixed full-screen wrappers.
- The background is a three.js scene. The terrain is one instanced mesh of 20 stacked planes (`aLayerOffset`). Each plane's fragment shader discards pixels where a height texture, `tHeightNoise`, is below that layer's level. Rainbow bands, shadows, highlights and contour lines are all derived from that height.
- `tHeightNoise` is a 256x256 render target that a noise shader regenerates every frame (`noiseGenerator.update()` in the layer's `animate()`). The mouse feeds a fluid simulation (`tFluid`) that disturbs the layer edges.
- Scroll runs through Lenis and a Theatre.js sequence. Keyframed tracks `title0show`, `title0hide`, `title1show` and `title1hide` fire the section title animations; `herohide` fades the hero.
- The site does not check `prefers-reduced-motion`.

This changes how cheaply concept 01 can be built: anything added to the height the layer shader reads becomes terrain, with the same terraces, rainbow edges and shadows. Text drawn into a height texture becomes terraced letters with no new lighting code.

## Decisions

**One technique for all three sections: a text height map.** Draw the headline into a canvas with the site's headline font, blur it into a soft height ramp, upload it as a texture, and add it to the height inside the layer shader's `sampleNoise` and shadow lookup, scaled by a `uTextAmount` uniform. Sample it in the layer shader at full resolution rather than baking it into the 256x256 `tHeightNoise`, which is too coarse for letter edges. Place it by projecting the DOM heading's bounding box onto the terrain plane, so the letters land where the heading is and follow resizes and the mobile layout.

**Font.** The concept images drifted to a rounded grotesk. The build keeps the site's heavy monospace headline face. `index.html` preloads `/fonts/GeistMono-Bold.woff2`; confirm in the browser which face the `h1` and `h2` actually render before drawing the height map.

**Color carries meaning.** Blocked means dark, allowed means light:

- Hero and Control letters: the terrain's own whites and greys with rainbow edges.
- Block letters: charcoal `#1a1a1a`, the color of the sections that follow, using a letter mask in the layer shader's color step.
- Chips: an Allow chip gets a thin rainbow edge; a Block chip is muted charcoal. No red and green, which concept 09 introduced and the site never uses.

**The Block wall is terraced first.** Concept 05 shows a solid extruded slab. A height-map letter is a stack of 20 terraces, which in charcoal should read as a solid wall from a distance. Build that first and compare it with the concept. If it doesn't hold up, the fallback is a real extruded text mesh (three.js `TextGeometry` with the font converted to typeface JSON, roughly 100 KB more), placed in the same scene.

**Chips are DOM, not WebGL.** Concept 09's chips are small frosted cards with text. Build them as HTML in a fixed layer with CSS 3D transforms at three depths, so text stays sharp and selectable and costs no shader work. No third-party logos: concept 09 used the Chrome, Steam and Windows marks. Use a plain monochrome glyph or none.

**Headings stay real.** The `h1` and `h2` stay in the DOM as real headings, visually hidden (the existing `.sw-static` pattern) only while the WebGL text is drawn. With reduced motion, no WebGL, or a failed font load, the current DOM text shows as it does today. Remove the heavy `text-shadow` from the DOM fallback only if it stays legible on the terrain without it.

**Hero tagline.** "A default-deny firewall for Windows. Decide which apps connect." moves into its own space at bottom left, as in concept 01, with no overlap at any width.

## Vocabulary

One name per thing in code, checks and review notes:

| Use | For | Replaces |
|---|---|---|
| hero | first screen, "Choose what connects" | `js-hero`, "hero section" |
| Block section | "Block by default" screen | title 0, `sw-block-title` screen |
| Control section | "Full Control" screen | title 1, `sw-control-title` screen |
| text height map | canvas texture of a headline added to terrain height | none (new) |
| chip | one app card with an Allow or Block rule | none (new) |

## Motion

| Section | In | Out |
|---|---|---|
| Hero | letters already raised at first paint | letters sink back into the terrain as `herohide` runs (`uTextAmount` 1 to 0) |
| Block section | on `title0show` the charcoal letters rise from the terrain and the terrain in front of them piles into tighter ridges | on `title0hide` they sink |
| Control section | on `title1show` the white letters rise; chips enter one after another and drift at speeds set by their depth | on `title1hide` letters sink and chips leave |

Drive `uTextAmount` and the chip timeline from the existing Theatre tracks, not from a second scroll listener, so the text stays in step with the camera.

## Chip content (owner decision)

Concept 09 used `chrome.exe Allow`, `steam.exe Allow`, `svchost.exe Allow`, `telemetry.exe Block`, `updater.exe Block`. `telemetry.exe` is not a real program, and naming a real vendor's program as blocked reads as a claim about it. The owner picks the final list.

## Build constraints

- No source exists, so every change is a patch on the live deployment's files: an anchored string replacement in the bundle, where each anchor must match exactly once or the patch fails, plus new standalone files where possible. A patched bundle gets a new hashed name (`/build/*` is cached `immutable` for a year) and `index.html` is updated to match.
- `tools/release/update-secwall-site.mjs` starts from the live deployment, so a deployed patch carries into later release updates. Run it with `--out` on the patched output before deploying, to prove it still finds and replaces every version string.
- New data or backend: none. Everything is static.
- Production deploys to secwall.org need the owner's go-ahead. Use a Vercel preview deployment for review.

## Build order

1. Patch tooling: download the live deployment (reuse the SHA-1-checked download in `update-secwall-site.mjs`), apply anchored patches, rename changed bundles, serve the result locally. Capture the unpatched site at 1440x900 and 390x844 as a baseline, and record its frame rate.
2. Text height map in the layer shader: the uniforms, the canvas texture, and the DOM-rect projection. Hero only, static. Check legibility against concept 01.
3. Hero motion and the tagline layout fix.
4. Block section: charcoal letter mask, rise and sink on the title tracks, ridge pile-up in front. Compare with concept 05 and decide whether the extruded-mesh fallback is needed.
5. Control section: white letters, then the chip layer with depth and drift.
6. Fallbacks and mobile: reduced motion, no WebGL, font load failure, 390x844 layout.
7. Checks, then a preview deploy for owner review.

## Checks

- Every headline is readable at 1440x900 and 390x844, at rest and at the midpoint of each transition.
- The page has the same headings in its accessibility tree as before (`h1` "Choose what connects", `h2` "Block by default", `h2` "Full Control").
- Reduced motion and WebGL-off both show the current DOM headlines with no blank screen.
- Frame rate and frame time on the terrain sections are within 10% of the baseline from step 1, on the same machine.
- No console errors. Every bundle patch anchor matched exactly once.
- `update-secwall-site.mjs --out` runs cleanly on the patched output.
