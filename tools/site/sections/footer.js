// Footer section: "LIVE every connection, refreshed each second" as a dark
// charcoal 3D wall of type above the footer (concept 05's treatment). "LIVE"
// stands as tall extruded letters on a terrain mound, so the footer terrain's
// contour lines pile up against its base; "every connection, refreshed each
// second" lies smaller in front of it, nearer the viewer, covering the foot
// of "LIVE".
//
// Built with window.SWText (tools/site/lib/swtext.js) on the footer terrain
// ("Layers Footer"). The page has no element with this text, so the section
// adds one: a <p data-sw-section="footer"> just before the <footer>, inside
// .js-footer-webgl-start (the 100dvh block that ends the page; it lays its
// children out from the bottom, so the footer keeps its place). footer.css
// sizes the two spans and keeps their text transparent from the moment the
// <p> is added: the 3D letters replace it, and the text stays in the
// accessibility tree. Each span gets a glyph mask; "LIVE" stands on its own
// box, and the smaller line keeps its box's size and x, moved down to the
// foot of "LIVE" on screen.
//
// Show/hide: the site's footer heading is the "SecureWall" wordmark. On wide
// screens it fades in (GSAP autoAlpha) once, the first time the footer comes
// in, and stays shown after; on phones it is never animated. So the pieces
// follow the wordmark's visible state (lowest computed opacity among it and
// its ancestors) times the part of it inside the viewport: they rise as the
// footer comes to rest and sink into the terrain as it scrolls away.
//
// Frames: the site stops rendering WebGL between .js-webgl-scroll-end and
// .js-footer-webgl-start and shows the footer terrain only in the footer;
// SWText.onFrame (name "Layers Footer") runs only while it renders. The meshes hang
// in the footer terrain's group, which the hero scene also draws, so while
// the pieces show, a requestAnimationFrame watchdog hides the meshes, the
// mound, the ink layer and the <p> once footer frames stop arriving.
//
// Camera: at the footer's rest (1440x900) it stands about 3 units from the
// terrain, looking down at about 53 degrees with a 39 degree lens, so the
// letters stand well clear of the near plane. They are re-placed while the footer comes in (the camera and the boxes
// move) and whenever the camera or the boxes move while shown.
(() => {
  "use strict";
  const SW = window.SWText;
  if (!SW) return;

  const NAME = "Layers Footer";

  // Colors, linear RGB. The site's post-process lifts dark tones toward
  // white, so the faces start near black to read as charcoal on screen.
  // Sides: a lighter grey at the top edge that SliceText darkens toward the
  // base, so the walls read apart from the faces.
  const FACE = [0.009, 0.009, 0.011];
  const SIDES = [0.1, 0.1, 0.11];
  const BEVEL = 1.6;
  // An ink layer (a page canvas of the faces' glyph masks, blended with
  // multiply over the WebGL canvas) darkens the faces by INK while the
  // letters stand fully, past the post-process lift; it fades in over the
  // last INK_FROM..1 of the rise.
  const INK = "rgb(150, 150, 152)";
  const INK_FROM = 0.97;
  const INK_DPR = 2;

  // Heights, mesh-local z. The mound's plateau is the terrain layer at
  // PLATEAU. "LIVE" rises above it by LIVE_RISE x the depth its glyphs
  // cover on the plateau (so the walls keep their size next to the letters
  // whatever the viewport and camera), its base buried BURY below it. The
  // line's face is SMALL_RISE above the plateau and its slab SMALL_DEPTH
  // deep: it lies in front of the plateau, over the mound's slope, where a
  // deep slab would show tall walls.
  const PLATEAU = 0.95;
  const LIVE_RISE = 0.2;
  const LIVE_SLICES = 40;
  const SMALL_RISE = 0.05;
  const SMALL_SLICES = 8;
  const BURY = 0.08;
  const SMALL_DEPTH = 0.035;

  // Mound under "LIVE": the terrain goes to the plateau's layer cut-off
  // where a blurred copy of the "LIVE" glyphs is high. Its mask covers the
  // "LIVE" text rect grown by MOUND_SPREAD_X of its width on each side,
  // MOUND_SPREAD_UP of its height above and MOUND_SPREAD_DOWN below (toward
  // the viewer); MOUND_BLUR is the blur radius as a fraction of the mound
  // canvas width. MOUND_GAIN: how far the slope reaches. MOUND_LOD: mip used.
  const MOUND_SPREAD_X = 0.3;
  const MOUND_SPREAD_UP = 0.35;
  const MOUND_SPREAD_DOWN = 1.0;
  const MOUND_BLUR = 0.07;
  const MOUND_GAIN = 2.6;
  const MOUND_LOD = 1;
  const MOUND_CANVAS = [512, 384];
  // The footer terrain around the letters is mostly at its top layer
  // already, so raising alone would not show: the mound carves a trench down
  // to the layer at MOUND_FLOOR around the glyphs and climbs from it to the
  // plateau at their base, so the contour lines step up against the letters.
  // On a portrait viewport the camera sees the trench's floor as one flat
  // grey pit, and the terrain there is lower to begin with: the mound only
  // raises, and the plateau's rims step down in front of the letters.
  const MOUND_FLOOR = 0.5;

  // The line's text top sits where the foot of "LIVE" meets the plateau on
  // screen, raised by OVERLAP x its line height so it covers the foot.
  // BASELINE: where the glyphs of "LIVE" end, as a fraction of its text
  // rect's height.
  const OVERLAP = 0.35;
  const BASELINE = 0.775;
  const MASK_PAD = 0.08;
  const MASK_EDGE = 0.05;

  // Sink speed (per second) toward the shown amount; on the way up the
  // letters follow it at once.
  const SPEED = 6;
  // Watchdog: requestAnimationFrame ticks without a footer frame before the
  // pieces hide.
  const STALE_TICKS = 4;
  // Re-place on a camera matrix change above this.
  const CAMERA_EPSILON = 1e-4;

  SW.onTerrain(
    layer => {
      const owner = {};
      const T = SW.three(layer);
      const range = document.createRange();
      let phrase = null; // the added <p>
      let wordmark = null;
      let chain = null; // elements whose opacity and visibility count
      let words = null; // [{ el, mask, proxy, text, rect }]
      let placed = false;
      let rise = 0;
      let lastShown = 0;
      let stop = null;
      let frame = 0;
      let frameSeen = false;
      let ticks = 0;
      let watching = false;
      let stalled = false;
      let a = null;
      let b = null;
      let ink = null;
      let inkKey = "";
      let inkShown = -1;
      let phraseShown = -1;
      let moundStrength = -1;
      let moundMap = null;
      const seen = new Float64Array(34);
      const boxSeen = new Float64Array(4);

      const proxyOf = mask => ({
        get canvas() {
          return mask.canvas;
        },
        get texture() {
          return mask.texture;
        },
        box: null,
        update: mask.update,
      });

      // The mound's mask: a blurred copy of the "LIVE" mask on a larger box.
      const mound = { canvas: document.createElement("canvas"), texture: null, box: null, map: null, key: "" };
      mound.canvas.width = MOUND_CANVAS[0];
      mound.canvas.height = MOUND_CANVAS[1];
      mound.texture = new T.Texture(mound.canvas);

      // Grows the "LIVE" text rect r into the mound box and redraws the mound
      // canvas from the "LIVE" mask, drawn over maskBox, when either changed.
      function updateMound(r, mask, maskBox) {
        const dx = r.width * MOUND_SPREAD_X;
        const up = r.height * MOUND_SPREAD_UP;
        const m = (mound.box = new DOMRect(r.left - dx, r.top - up, r.width + 2 * dx, r.height * (1 + MOUND_SPREAD_UP + MOUND_SPREAD_DOWN)));
        const { width: cw, height: ch } = mound.canvas;
        const x = ((maskBox.left - m.left) / m.width) * cw;
        const y = ((maskBox.top - m.top) / m.height) * ch;
        const w = (maskBox.width / m.width) * cw;
        const h = (maskBox.height / m.height) * ch;
        const tex = mask.texture;
        const key = `${tex.uuid}|${tex.version}|${Math.round(x)}|${Math.round(y)}|${Math.round(w)}|${Math.round(h)}`;
        if (key === mound.key) return;
        mound.key = key;
        const ctx = mound.canvas.getContext("2d");
        // Blur by casting the glyphs' shadow back from off the left edge.
        const shift = cw * 2;
        ctx.clearRect(0, 0, cw, ch);
        ctx.shadowColor = "#000";
        ctx.shadowOffsetX = shift;
        ctx.shadowBlur = MOUND_BLUR * cw;
        ctx.drawImage(mask.canvas, x - shift, y, w, h);
        ctx.shadowBlur = MOUND_BLUR * cw * 0.4;
        ctx.drawImage(mask.canvas, x - shift, y, w, h);
        ctx.shadowColor = "transparent";
        mound.texture.needsUpdate = true;
      }

      const textRect = el => {
        range.selectNodeContents(el);
        return range.getBoundingClientRect();
      };

      // Adds the phrase before the footer; false while the footer is not in
      // the page yet.
      function setup() {
        if (phrase) return true;
        const footer = document.querySelector("footer.js-footer");
        const host = footer?.parentElement;
        wordmark = footer?.querySelector(".sw-footer-wordmark") ?? null;
        if (!host || !wordmark) return false;
        phrase = document.createElement("p");
        phrase.className = "sw-footer-phrase";
        phrase.dataset.swSection = "footer";
        phrase.style.color = "transparent";
        const live = document.createElement("span");
        live.className = "sw-footer-live";
        live.dataset.swSection = "footer";
        live.textContent = "LIVE";
        const line = document.createElement("span");
        line.className = "sw-footer-line";
        line.dataset.swSection = "footer";
        line.textContent = "every connection, refreshed each second";
        phrase.append(live, " ", line);
        host.insertBefore(phrase, footer);
        showPhrase(0);
        chain = [];
        for (let e = wordmark; e && e !== document.documentElement; e = e.parentElement) chain.push(e);
        return true;
      }

      function build() {
        const [live, line] = phrase.children;
        words = [
          // The top of "LIVE" is set from the camera before each place().
          ["live", live, PLATEAU + 0.2, PLATEAU - BURY, LIVE_SLICES],
          ["line", line, PLATEAU + SMALL_RISE, PLATEAU + SMALL_RISE - SMALL_DEPTH, SMALL_SLICES],
        ].map(([name, el, topZ, baseZ, slices]) => {
          const mask = SW.glyphMask(el, { pad: MASK_PAD, edge: MASK_EDGE });
          const proxy = proxyOf(mask);
          const text = new SW.SliceText(layer, {
            mask: proxy,
            slices,
            baseZ,
            topZ,
            face: FACE,
            sides: SIDES,
            bevel: BEVEL,
          });
          text.mesh.name = `sw-footer-${name}`;
          return { el, mask, proxy, text, rect: null };
        });
      }

      // View ray through client point (x, y), mesh-local (the terrain mesh's
      // world matrix is identity): a on the near plane, b on the far plane.
      function ray(x, y, camera, canvas) {
        a ||= new T.Vector3();
        b ||= new T.Vector3();
        const nx = ((x - canvas.left) / canvas.width) * 2 - 1;
        const ny = 1 - ((y - canvas.top) / canvas.height) * 2;
        a.set(nx, ny, -1).unproject(camera);
        b.set(nx, ny, 1).unproject(camera);
      }

      // Client y where the vertical line below client point (x, y) on the
      // plane at z1 meets the plane at z2.
      function dropY(x, y, z1, z2, camera, canvas) {
        ray(x, y, camera, canvas);
        const k = (z1 - a.z) / (b.z - a.z);
        a.lerp(b, k);
        a.z = z2;
        a.project(camera);
        return canvas.top + ((1 - a.y) / 2) * canvas.height;
      }

      function buildInk() {
        ink = document.createElement("canvas");
        ink.className = "sw-footer-ink";
        ink.setAttribute("aria-hidden", "true");
        ink.dataset.swSection = "footer";
        document.body.append(ink);
      }

      // Redraws the ink layer when a face box, a mask or the viewport changed.
      function drawInk() {
        const k = Math.min(INK_DPR, devicePixelRatio || 1);
        const W = Math.round(innerWidth * k);
        const H = Math.round(innerHeight * k);
        let key = `${W}x${H}`;
        for (const w of words) {
          const r = w.proxy.box;
          key += `|${w.mask.texture.version}|${Math.round(r.x * k)},${Math.round(r.y * k)},${Math.round(r.width * k)},${Math.round(r.height * k)}`;
        }
        if (key === inkKey) return;
        inkKey = key;
        if (ink.width !== W || ink.height !== H) {
          ink.width = W;
          ink.height = H;
        }
        const ctx = ink.getContext("2d");
        ctx.globalCompositeOperation = "source-over";
        ctx.clearRect(0, 0, W, H);
        for (const w of words) {
          const r = w.proxy.box;
          ctx.drawImage(w.mask.canvas, r.x * k, r.y * k, r.width * k, r.height * k);
        }
        ctx.globalCompositeOperation = "source-in";
        ctx.fillStyle = INK;
        ctx.fillRect(0, 0, W, H);
        ctx.globalCompositeOperation = "source-over";
      }

      // Places both words and the mound. true when placed, false when a word
      // cannot be mapped, null while a word has no size.
      function placeAll(camera, renderer) {
        for (const w of words) if (!w.mask.update()) return null;
        const canvas = renderer.domElement.getBoundingClientRect();
        const [live, line] = words;
        const tl = textRect(live.el);
        const tn = textRect(line.el);
        live.proxy.box = live.mask.box;
        live.rect = tl;
        // Depth of the glyphs on the plateau (mesh-local y), and the rise.
        const cx = tl.left + tl.width / 2;
        ray(cx, tl.top, camera, canvas);
        let k = (PLATEAU - a.z) / (b.z - a.z);
        const y0 = a.y + (b.y - a.y) * k;
        ray(cx, tl.top + tl.height * BASELINE, camera, canvas);
        k = (PLATEAU - a.z) / (b.z - a.z);
        const depth = Math.abs(a.y + (b.y - a.y) * k - y0);
        if (!(depth > 0 && Number.isFinite(depth))) return false;
        live.text.topZ = PLATEAU + LIVE_RISE * depth;
        // The line: same size and x, its text top on the foot of "LIVE".
        const foot = dropY(cx, tl.top + tl.height * BASELINE, live.text.topZ, PLATEAU, camera, canvas);
        const dy = foot - OVERLAP * Math.min(tn.height, parseFloat(getComputedStyle(line.el).lineHeight) || tn.height) - tn.top;
        const m = line.mask.box;
        line.proxy.box = new DOMRect(m.x, m.y + dy, m.width, m.height);
        line.rect = new DOMRect(tn.x, tn.y + dy, tn.width, tn.height);
        let ok = true;
        for (const w of words) ok = w.text.place(camera, renderer) && ok;
        if (!ok) return false;
        updateMound(live.rect, live.mask, live.proxy.box);
        mound.map = SW.planeMap(camera, renderer, mound.box, live.text.topZ, layer) ?? mound.map;
        if (!ink) buildInk();
        drawInk();
        return true;
      }

      function cameraMoved(camera) {
        const e = camera.matrixWorld.elements;
        const p = camera.projectionMatrix.elements;
        let moved = seen[32] !== innerWidth || seen[33] !== innerHeight;
        for (let i = 0; i < 16 && !moved; i++) {
          if (Math.abs(seen[i] - e[i]) > CAMERA_EPSILON || Math.abs(seen[16 + i] - p[i]) > CAMERA_EPSILON * (1 + Math.abs(p[i]))) moved = true;
        }
        if (!moved) return false;
        for (let i = 0; i < 16; i++) {
          seen[i] = e[i];
          seen[16 + i] = p[i];
        }
        seen[32] = innerWidth;
        seen[33] = innerHeight;
        return true;
      }

      // True when the "LIVE" span's box moved by half a pixel or more.
      function boxMoved() {
        const r = words[0].el.getBoundingClientRect();
        const v = [r.x, r.y, r.width, r.height];
        let moved = false;
        for (let i = 0; i < 4; i++) {
          if (Math.abs(boxSeen[i] - v[i]) >= 0.5) {
            moved = true;
            boxSeen[i] = v[i];
          }
        }
        return moved;
      }

      // The wordmark's lowest opacity times the part of its height inside
      // the viewport.
      function shownAmount() {
        let min = 1;
        for (const e of chain) {
          const css = getComputedStyle(e);
          if (css.visibility === "hidden" || css.display === "none") return 0;
          const o = parseFloat(css.opacity);
          if (o < min) min = o;
        }
        if (!(min > 0)) return 0;
        const r = wordmark.getBoundingClientRect();
        if (!(r.height > 0)) return 0;
        const inside = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0);
        return min * Math.min(1, Math.max(0, inside / r.height));
      }

      function showInk(amount) {
        inkShown = amount;
        ink.style.opacity = String(amount);
      }

      // The <p>'s opacity follows the letters (its text stays transparent).
      function showPhrase(amount) {
        phraseShown = amount;
        phrase.style.opacity = String(amount);
      }

      // Hides every piece at once (frames stopped).
      function hideAll() {
        rise = 0;
        if (words) for (const w of words) w.text.setAmount(0);
        if (ink) showInk(0);
        showPhrase(0);
        moundStrength = 0;
        SW.setMound(owner, { mask: mound, map: mound.map, strength: 0, layer });
      }

      // While a piece shows: counts animation frames without a footer frame
      // and hides everything after STALE_TICKS of them.
      function watch() {
        if (!(rise > 0 || inkShown > 0 || phraseShown > 0)) {
          watching = false;
          return;
        }
        if (frameSeen) {
          frameSeen = false;
          ticks = 0;
        } else if (++ticks >= STALE_TICKS) {
          stalled = true;
          hideAll();
          watching = false;
          return;
        }
        requestAnimationFrame(watch);
      }

      // Puts the page back as it was; SWText logs the error.
      function fail(err) {
        stop?.();
        phrase?.remove();
        ink?.remove();
        SW.clearMound(owner);
        for (const w of words ?? []) w.text.dispose();
        mound.texture.dispose();
        throw err;
      }

      stop = SW.onFrame(
        ({ renderer, camera, dt }) => {
          try {
            frameSeen = true;
            if (!setup()) return;
            if (!words) build();
            const shown = shownAmount();
            const rising = shown > lastShown;
            lastShown = shown;
            frame++;
            if (shown > 0) {
              const moved = cameraMoved(camera);
              const boxed = boxMoved();
              if (moved || boxed || rising || !placed || frame % 30 === 0) {
                const result = placeAll(camera, renderer);
                if (result) placed = true;
              }
            }
            const target = placed ? shown : 0;
            // Back from a stall: no higher than the footer is now.
            if (stalled) {
              stalled = false;
              rise = Math.min(rise, target);
            }
            rise = target > rise ? target : rise + (target - rise) * Math.min(1, dt * SPEED);
            if (rise < 0.001) rise = 0;
            const eased = rise * rise * (3 - 2 * rise);
            for (const w of words) if (eased !== w.text.amount) w.text.setAmount(eased);
            const step = Math.round(eased * 100) / 100;
            if (step !== phraseShown) showPhrase(step);
            if (ink) {
              const t = Math.min(1, Math.max(0, (eased - INK_FROM) / (1 - INK_FROM)));
              const shownInk = target > 0 ? Math.round(t * t * (3 - 2 * t) * 100) / 100 : 0;
              if (shownInk !== inkShown) showInk(shownInk);
            }
            if (placed && mound.map && (eased !== moundStrength || mound.map !== moundMap)) {
              moundStrength = eased;
              moundMap = mound.map;
              SW.setMound(owner, { mask: mound, map: mound.map, strength: eased, level: PLATEAU, lod: MOUND_LOD, gain: MOUND_GAIN, floor: innerHeight > innerWidth ? undefined : MOUND_FLOOR, layer });
            }
            if (!watching && (rise > 0 || inkShown > 0 || phraseShown > 0)) {
              watching = true;
              ticks = 0;
              requestAnimationFrame(watch);
            }
          } catch (err) {
            fail(err);
          }
        },
        { name: NAME },
      );

      // The phrase goes in now if the footer is already in the page, else on
      // a later animation frame. Errors here are SWText's to report (it logs
      // the one inside onTerrain); a later one gets the same single warning.
      const add = () => {
        try {
          if (!setup()) requestAnimationFrame(add);
        } catch (err) {
          try {
            fail(err);
          } catch {
            console.warn("SWText disabled:", err);
          }
        }
      };
      add();
    },
    { name: NAME },
  );
})();
