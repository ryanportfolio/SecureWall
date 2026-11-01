# secwall.org headline redesign

Status: all three sections built as stacked-slice 3D text (see "How it is built"), reviewed locally and on a Vercel preview only; production secwall.org is unchanged. The owner rejected the first hero build (flat plates with a neon rim); the "Decisions" section below is the original plan, and "How it is built" records what replaced it. Written 2026-10-31 from a `/redesign-concepts` run.

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

**One technique for all three sections: a text height map.** Draw the headline into a canvas with the site's headline font, blur it into a soft edge, upload it as a texture, and feed it into the height inside the layer shader's `sampleNoise` and shadow lookup. Sample it in the layer shader at full resolution rather than baking it into the 256x256 `tHeightNoise`, which is too coarse for letter edges. Place it by projecting the DOM heading's bounding box onto the terrain plane, so the letters land where the heading is and follow resizes and the mobile layout.

**Font.** The concept images drifted to a rounded grotesk. The build keeps the site's heavy monospace headline face. `index.html` preloads `/fonts/GeistMono-Bold.woff2`; confirm in the browser which face the `h1` and `h2` actually render before drawing the height map.

**Color carries meaning.** Blocked means dark, allowed means light:

- Hero and Control letters: the terrain's own whites and greys with rainbow edges.
- Block letters: charcoal `#1a1a1a`, the color of the sections that follow, using a letter mask in the layer shader's color step.
- Chips: an Allow chip gets a thin rainbow edge; a Block chip is muted charcoal. No red and green, which concept 09 introduced and the site never uses.

**The Block wall needs real geometry.** Concept 05 shows a solid extruded slab. The hero build showed that terraced letters do not work in this scene (see "What the hero build showed"), so the wall needs a real extruded text mesh (three.js `TextGeometry` with the font converted to typeface JSON, roughly 100 KB more) placed in the same scene. The owner also likes concept 06, the charcoal panel with the words cut out as windows onto the terrain, and may switch to it after seeing the hero live. Concept 06 needs no 3D text: a DOM or SVG mask over the canvas.

**Chips are DOM, not WebGL.** Concept 09's chips are small frosted cards with text. Build them as HTML in a fixed layer with CSS 3D transforms at three depths, so text stays sharp and selectable and costs no shader work. No third-party logos: concept 09 used the Chrome, Steam and Windows marks. Use a plain monochrome glyph or none.

**Headings stay real.** The `h1` and `h2` stay in the DOM as real headings, visually hidden (the existing `.sw-static` pattern) only while the WebGL text is drawn. With reduced motion, no WebGL, or a failed font load, the current DOM text shows as it does today. Remove the heavy `text-shadow` from the DOM fallback only if it stays legible on the terrain without it.

**Hero tagline.** "A default-deny firewall for Windows. Decide which apps connect." moves into its own space at bottom left, as in concept 01, with no overlap at any width.

## What the hero build showed

- The terrain instance is named `Layers Hero` (the footer has its own, `Layers Footer`). The camera sits about 0.23 units above the top layer, close and steep, so layers 0.05 apart land about 80 px apart on screen.
- Adding height to the terrain does not make terraced letters. Each letter's top lands on whatever layer the ground under it reaches, and every layer a letter crosses prints its own offset copy. Readable letters must sit on a single layer, as a flat plate at a fixed level.
- An affine map from three box corners is far off on this camera; the text needs a four-corner homography onto the top-layer plane.
- The shader wobbles uv by up to 0.024 (`heightNoiseOffset`), about a fifth of the headline's width in uv, so the text is looked up without it.
- The scene's post-processing lifts the bright side of the terrain toward white after the layer shader runs, so darkening (shadows) does not separate white letters there. A rainbow rim around the letters, in the terrain's own band colors, does.
- The hero's intro animation translates the characters, so the canvas is redrawn whenever a character moves.

## How it is built

The text height map was dropped after the owner compared the first hero with concept 01. Each headline is now real 3D geometry in the terrain scene:

- **Library** `tools/site/lib/swtext.js` (`window.SWText`). It takes three.js classes from objects the site already built, so nothing new is downloaded. It draws a headline's glyphs into a canvas mask (`glyphMask`), maps the DOM box onto the terrain plane with a four-corner homography (`planeMap`), and builds extruded letters as an `InstancedMesh` of stacked planes that each discard outside the glyphs (`SliceText`). It also patches the terrain shader once to raise a mound under a mask (`setMound`, with an optional trench around it), so the terrain's contour lines bend around the letters. It attaches to both terrain instances, `Layers Hero` and `Layers Footer`; calls without a terrain name or layer mean the hero. The site's Theatre sheet resets every material uniform when its animation values change, so the library rewrites the mound uniforms before each frame. The header comment documents the API.
- **Hero** `tools/site/sections/hero.js`: white bevelled faces with rainbow-striped walls (`sw-hero`), grey contour terraces offset from the glyph outlines (`sw-hero-rings`, built from a distance transform of the mask), and a mound whose inside corners are rounded off so no dark terrain slivers show through. The h1 is set at 190 px in three centred lines at 1024 px and wider.
- **Block** `tools/site/sections/block.js` (concept 06, picked by the owner): a charcoal panel with "Block by default" cut out of it, drawn on one page canvas over the WebGL canvas (`.sw-block-panel`, marked `data-sw-section="block"`). The live terrain shows through the letter holes; each hole has a cut wall on its top and left edges and casts a soft shadow inside. The panel slides up from below as the title shows and moves with a CSS transform; it is redrawn only when the viewport, font or text changes. No 3D mesh.
- **Footer** `tools/site/sections/footer.js` (concept 05's treatment, moved from Block): "LIVE" as tall dark charcoal extruded letters (`sw-footer-live`) on the footer terrain, and "every connection, refreshed each second" smaller in front over its foot (`sw-footer-line`). The page has no element with this text, so the section adds a `<p data-sw-section="footer">` before the `<footer>`, with transparent text, readable by screen readers. A mound raises the terrain under "LIVE"; on desktop it first carves a trench so the contours visibly climb, because most of the footer terrain is already at its top layer. The site's post-processing lifts black to about 77/255, so a multiply-blended ink canvas darkens the faces to about 47-54/255. It follows the footer wordmark's opacity times how much of it is in view, because the wordmark fades in once and never hides again.
- **Control** `tools/site/sections/control.js`: white letters (`sw-control`) kept a fixed distance below the camera, plus six frosted DOM cards from the `CARDS` array (generic placeholder names, not real products). Allow is a cyan tick and Block a violet ring; there is no red or green. Depth comes from scale and blur.
- **Shared rules**: every section follows its heading's own show and hide, so the 3D pieces appear only in that section. It hides the DOM heading with `color: transparent` as soon as the section is set up, so the flat heading never shows before the 3D version, and the heading stays in the accessibility tree. Fixed DOM pieces (Control's cards, Block's panel, the footer's ink canvas and phrase) hide when the site stops rendering WebGL. `prefers-reduced-motion` gets the same version (owner decision); only a setup failure, such as no WebGL, leaves the DOM headings showing.

Known limits: the site's chromatic aberration softens letter edges, most on a phone. In concept 01 the terrain's own coloured contours hug each letter; here grey terraces hug them and the coloured bands bend further out. Firefox and WebKit at device pixel ratio 1 render like Chrome. Headless WebKit at ratio 2 drew a blank terrain on the unpatched site too, and there the patch also hides the headline: check on a real iPhone or in Safari. Not tested on real phones. The footer's own post-processing blurs and colour-fringes more than the hero's, so "LIVE" has soft top edges, and on a phone the small line wraps to two lines over the foot of "LIVE".

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
| Hero | letters rise to the top layer once the hero shows | letters sink to a low layer as `herohide` fades the hero; built to follow the hero's computed opacity, which `herohide` drives |
| Block section | on `title0show` the charcoal letters rise from the terrain and the terrain in front of them piles into tighter ridges | on `title0hide` they sink |
| Control section | on `title1show` the white letters rise; chips enter one after another and drift at speeds set by their depth | on `title1hide` letters sink and chips leave |

Drive `uTextAmount` and the chip timeline from the existing Theatre tracks, not from a second scroll listener, so the text stays in step with the camera.

## Chip content (owner decision)

Concept 09 used `chrome.exe Allow`, `steam.exe Allow`, `svchost.exe Allow`, `telemetry.exe Block`, `updater.exe Block`. `telemetry.exe` is not a real program, and naming a real vendor's program as blocked reads as a claim about it. The owner picks the final list.

## Build constraints

- No source exists, so every change is a patch on the live deployment's files: an anchored string replacement in the bundle, where each anchor must match exactly once or the patch fails, plus new standalone files where possible. A patched bundle gets a new hashed name (`/build/*` is cached `immutable` for a year) and `index.html` is updated to match.
- `tools/release/update-secwall-site.mjs` starts from the live deployment, so a deployed patch carries into later release updates. It cannot check a local patched build as it stands: `update()` always downloads production, and `downloadLive()` deletes the `--out` directory first (`fs.rmSync(out, { recursive: true, force: true })`). Never point `--out` at the patched output. Before deploying, run the updater's version replacement and checks against the patched files through a local-input mode (for example `--from <dir>`, which reads a directory instead of downloading and writes to a separate `--out`). Adding that mode is part of step 1 and changes a release script, so it gets its own review.
- New data or backend: none. Everything is static.
- Production deploys to secwall.org need the owner's go-ahead. Use a Vercel preview deployment for review.

## Build order

1. Patch tooling: download the live deployment (reuse the SHA-1-checked download in `update-secwall-site.mjs`), apply anchored patches, rename changed bundles, serve the result locally. Add the updater's local-input mode. Capture the unpatched site at 1440x900 and 390x844 as a baseline, and record its frame rate.
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
- `update-secwall-site.mjs` in local-input mode runs cleanly on a copy of the patched output, written to a separate directory, and leaves the patched output untouched.
