// Block section: "Block by default" as a dark charcoal 3D wall of type.
// "Block" stands as tall extruded letters on a terrain mound, so the contour
// lines pile up against its base; "by default" sits smaller on one line in
// front of it, nearer the viewer, covering the foot of "Block".
//
// Built with window.SWText (tools/site/lib/swtext.js). The h2 keeps its DOM
// text and per-word animation; each word gets a glyph mask, and the 3D layout
// (sizes, one line for "by default") comes from moving and scaling each mask's
// box, so the page's own layout is never changed. The pieces follow the h2's
// visible state (lowest computed opacity among the h2, its ancestors and its
// words): they rise while it shows and sink into the terrain as it hides. They
// are re-placed while the title is at least half shown and the camera moves,
// so they stay on the title's box and keep their size on screen; while hiding
// they stay put in the scene. Once drawn, <html> gets sw-block-3d and
// block.css makes the DOM h2 transparent.
//
// Depth: the scroll camera drops from about 0.17 to 0.10 above the top
// terrain layer while the title shows, and widens its lens from about 89 to
// 110 degrees. All heights are set from the distance D between the camera and
// the face of "Block": D follows the camera's height (BLOCK_DIST of it) while
// that keeps every face corner clear of the near plane, and grows past it when
// it would not, so the letters move down into the terrain instead of being
// cut. The mound's plateau (snapped to a terrain layer, D grown to match), the
// walls and "by default" are set from D, so the look keeps its proportions.
// "by default" stands on the plateau at the foot of "Block", so the mound
// never covers it. Only if even that fails (a camera too close to
// the terrain floor) do the letters sink and <html> also gets sw-block-flat,
// which shows the DOM h2 again.
//
// Concept 06 (a charcoal panel with cut-out letters) would reuse the colors,
// heights and mound settings below.
(() => {
  "use strict";
  const SW = window.SWText;
  if (!SW) return;

  // Colors, linear RGB. The site's post-process lifts dark tones toward white,
  // so the faces start near black to read as charcoal on screen. Sides: a
  // lighter grey at the top edge that SliceText darkens toward the base, so
  // the walls read apart from the faces.
  const FACE = [0.009, 0.009, 0.011];
  const SIDES = [0.15, 0.15, 0.165];
  const SMALL_FACE = [0.009, 0.009, 0.011];
  const SMALL_SIDES = [0.15, 0.15, 0.165];
  const BEVEL = 1.6;
  // The same post-process lifts black to about 30% grey, lighter than the
  // concept's charcoal. An ink layer (a page canvas of the faces' glyph
  // masks, blended with multiply over the WebGL canvas) darkens the faces by
  // INK while the letters stand fully; it fades in over the last INK_FROM..1
  // of the rise, while the faces are still moving into place.
  const INK = "rgb(150, 150, 152)";
  const INK_FROM = 0.97;
  const INK_DPR = 2;

  // Heights, mesh-local z, as multiples of D (see above). "Block" rises
  // BLOCK_RISE x D above the mound's plateau; "by default" is a low slab
  // SMALL_RISE x D above it; both bases are buried BURY x D below it.
  const TERRAIN_TOP = 0.95;
  const BLOCK_DIST = 0.72;
  const BLOCK_RISE = 0.39;
  const BLOCK_SLICES = 48;
  const SMALL_RISE = 0.05;
  const SMALL_SLICES = 14;
  const BURY = 0.2;
  // Every face corner stays at least NEAR_MARGIN x the near plane distance
  // from the camera (view depth).
  const NEAR_MARGIN = 1.35;
  // Lowest plateau the letters may stand on; below it they sink (flat).
  const MIN_LEVEL = 0.12;
  // Terrain layers sit every LAYER_STEP in z, the top one at TERRAIN_TOP.
  // setMound raises the height by MOUND_TOP past a layer's cut-off.
  const LAYER_STEP = 0.05;
  const MOUND_TOP = 0.01;
  // On a portrait viewport the camera sees the mound's front slope from close
  // by, and a plateau at the top layers stacks many layer rims there into
  // grey bands. The plateau goes this many layers lower, nearer the terrain
  // around it, so only a few rims remain to curve around the letters' feet.
  const PORTRAIT_DROP = 4;

  // Mound under "Block": the terrain rises toward the plateau's layer cut-off
  // where a blurred copy of the "Block" glyphs is high, so the contour lines
  // pile up against the letters' base. The mound's own mask covers the
  // "Block" box grown by MOUND_SPREAD_X of its width on each side,
  // MOUND_SPREAD_UP of its height above and MOUND_SPREAD_DOWN below (toward
  // the viewer); MOUND_BLUR is the blur radius as a fraction of the mound
  // canvas width. MOUND_GAIN: how far the slope reaches (higher = steeper,
  // wider plateau). MOUND_LOD: mip used.
  const MOUND_SPREAD_X = 0.2;
  const MOUND_SPREAD_UP = 0.15;
  const MOUND_SPREAD_DOWN = 0.7;
  const MOUND_BLUR = 0.16;
  const MOUND_GAIN = 1.6;
  const MOUND_LOD = 1;
  const MOUND_CANVAS = [512, 384];

  // 3D layout, relative to the DOM title. "Block" is scaled by BLOCK_SCALE
  // (capped to MAX_WIDTH of the viewport) and keeps the top of its line;
  // "by default" uses SMALL_RATIO of that size, one line, centred. Its text
  // top sits where the foot of "Block" meets the plateau on screen, raised by
  // OVERLAP x its own line height so it covers the foot. BASELINE: where the
  // glyphs of "Block" end, as a fraction of its text rect's height.
  const BLOCK_SCALE = 1.55;
  const SMALL_RATIO = 0.4;
  const OVERLAP = 0.3;
  const BASELINE = 0.78;
  const MAX_WIDTH = 0.9;
  const MASK_PAD = 0.08;
  const MASK_EDGE = 0.05;

  // Rise/sink speed (per second) toward the title's visible state.
  const SPEED = 6;
  // The letters follow the title's box (and the camera) while the title is at
  // least this visible; below it, while it hides, they stay put in the scene.
  // The words' slide-in is horizontal and the layout is centred, so this
  // does not make the letters slide.
  const PLACE_FROM = 0.5;
  // Camera matrix change that triggers a re-place while the title shows.
  const CAMERA_EPSILON = 1e-4;

  SW.onTerrain(layer => {
    const owner = {};
    const range = document.createRange();
    const T = SW.three(layer);
    let title = null;
    let chain = null; // elements whose opacity and visibility count
    let words = null; // [{ el, mask, proxy, text, rise }]
    let placed = false;
    let drawn = false;
    let rise = 0;
    let frame = 0;
    let level = TERRAIN_TOP;
    let moundStrength = -1;
    let moundMap = null;
    let moundLevel = -1;
    let ok = true;
    let isFlat = false;
    let a = null;
    let b = null;
    let ink = null;
    let inkKey = "";
    let inkShown = -1;
    const seen = new Float64Array(34);

    // A mask whose box is moved and scaled; SliceText and setMound read only
    // canvas, texture and box.
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

    // The mound's mask: a blurred copy of the "Block" mask on a larger box.
    const mound = { canvas: document.createElement("canvas"), texture: null, box: null, map: null, key: "" };
    mound.canvas.width = MOUND_CANVAS[0];
    mound.canvas.height = MOUND_CANVAS[1];
    mound.texture = new T.Texture(mound.canvas);

    // Grows the "Block" text rect into the mound box and redraws the mound
    // canvas when the "Block" mask or its place in the box changed.
    function updateMound(mask, maskBox, r) {
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

    function build() {
      const divs = [...title.children];
      const byText = t => divs.find(d => d.textContent.trim() === t);
      const parts = [
        ["block", byText("Block"), BLOCK_RISE, BLOCK_SLICES, FACE, SIDES],
        ["by", byText("by"), SMALL_RISE, SMALL_SLICES, SMALL_FACE, SMALL_SIDES],
        ["default", byText("default"), SMALL_RISE, SMALL_SLICES, SMALL_FACE, SMALL_SIDES],
      ];
      if (parts.some(p => !p[1])) throw new Error("block title words not found");
      words = parts.map(([name, el, rise, slices, face, sides]) => {
        const mask = SW.glyphMask(el, { pad: MASK_PAD, edge: MASK_EDGE });
        const proxy = proxyOf(mask);
        // Heights are set from the camera before each place().
        const text = new SW.SliceText(layer, { mask: proxy, slices, baseZ: TERRAIN_TOP - 0.01, topZ: TERRAIN_TOP + 0.01, face, sides, bevel: BEVEL });
        text.mesh.name = `sw-block-${name}`;
        return { el, mask, proxy, text, rise };
      });
      chain = [title, ...title.querySelectorAll("*")];
      for (let e = title.parentElement; e && e !== document.documentElement; e = e.parentElement) chain.push(e);
    }

    // Mask box and text rect for a word whose text rect t moves to (x, y) at
    // scale k.
    function setBox(w, t, x, y, k) {
      const m = w.mask.box;
      w.proxy.box = new DOMRect(x + (m.left - t.left) * k, y + (m.top - t.top) * k, m.width * k, m.height * k);
      w.rect = new DOMRect(x, y, t.width * k, t.height * k);
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

    // Smallest drop below the camera (mesh-local z) at which a plane keeps
    // every corner of rect r NEAR_MARGIN x the near distance away in view
    // depth. Along a view ray both view depth and the drop below the camera
    // grow linearly from 0, so their ratio is fixed per corner. Infinity when
    // a corner's ray does not go down.
    function dropFor(r, camera, canvas) {
      let need = 0;
      for (let i = 0; i < 4; i++) {
        ray(r.left + (i & 1 ? r.width : 0), r.top + (i & 2 ? r.height : 0), camera, canvas);
        const fall = a.z - b.z; // drop from the near point to the far point
        if (!(fall > 0)) return Infinity;
        // Depth per unit drop, measured from the near point to the far one.
        const perDrop = (camera.far - camera.near) / fall;
        need = Math.max(need, (NEAR_MARGIN * camera.near) / perDrop);
      }
      return need;
    }

    // Client y where the vertical line below client point (x, y) on the plane
    // at z1 meets the plane at z2.
    function dropY(x, y, z1, z2, camera, canvas) {
      ray(x, y, camera, canvas);
      const k = (z1 - a.z) / (b.z - a.z);
      a.lerp(b, k);
      a.z = z2;
      a.project(camera);
      return canvas.top + ((1 - a.y) / 2) * canvas.height;
    }

    // The ink layer: decorative, added once the letters first place.
    function buildInk() {
      ink = document.createElement("canvas");
      ink.className = "sw-block-ink";
      ink.setAttribute("aria-hidden", "true");
      ink.dataset.swSection = "block";
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

    // Lays the words out, sets every height from the camera and places the
    // meshes. true when placed, false when the letters cannot stand, null
    // while a word has no size (nothing changes).
    function placeAll(camera, renderer) {
      for (const w of words) if (!w.mask.update()) return null;
      const canvas = renderer.domElement.getBoundingClientRect();
      const camZ = camera.matrixWorld.elements[14];
      const vw = innerWidth;
      const [block, by, def] = words;
      const tb = textRect(block.el);
      const ty = textRect(by.el);
      const td = textRect(def.el);
      const space = td.width / 7; // one monospace advance
      const s = Math.min(BLOCK_SCALE, (MAX_WIDTH * vw) / tb.width);
      const kc = Math.min(s * SMALL_RATIO, (MAX_WIDTH * vw) / (ty.width + space + td.width));
      const cx = vw / 2;
      // Block: centered, keeping the top of its line.
      setBox(block, tb, cx - (tb.width * s) / 2, tb.top, s);
      // Smallest distance from the camera down to the face of "Block".
      let d = Math.max(BLOCK_DIST * (camZ - TERRAIN_TOP), (camZ - TERRAIN_TOP) / (1 + BLOCK_RISE), dropFor(block.rect, camera, canvas));
      // by default: one line, centered, its top on the foot of "Block" (where
      // the foot meets the plateau on screen, which does not depend on d).
      const r = block.rect;
      const foot = dropY(cx, r.top + r.height * BASELINE, camZ - d, camZ - (1 + BLOCK_RISE) * d, camera, canvas);
      const y = foot - OVERLAP * td.height * kc;
      const lw = (ty.width + space + td.width) * kc;
      const x0 = cx - lw / 2;
      setBox(by, ty, x0, y, kc);
      setBox(def, td, x0 + (ty.width + space) * kc, y, kc);
      // Its face lies (1 + BLOCK_RISE - SMALL_RISE) x d below the camera.
      const small = Math.max(dropFor(by.rect, camera, canvas), dropFor(def.rect, camera, canvas));
      d = Math.max(d, small / (1 + BLOCK_RISE - SMALL_RISE));
      // The plateau lands on a terrain layer (layers sit every LAYER_STEP in
      // z), at or below where d puts it; d grows to match. The face of
      // "Block" keeps its place on screen, so this does not show.
      const plateau = LAYER_STEP * Math.floor((camZ - (1 + BLOCK_RISE) * d) / LAYER_STEP + 1e-6) - (innerHeight > innerWidth ? PORTRAIT_DROP * LAYER_STEP : 0);
      d = (camZ - plateau) / (1 + BLOCK_RISE);
      if (!Number.isFinite(d) || plateau < MIN_LEVEL) return false;
      let ok = true;
      for (const w of words) {
        w.text.topZ = plateau + w.rise * d;
        w.text.baseZ = plateau - BURY * d;
        ok = w.text.place(camera, renderer) && ok;
      }
      if (!ok) return false;
      // The mound fills layers up to level + MOUND_TOP / uDepthOffset (the
      // library adds MOUND_TOP to the height): set it halfway between the
      // plateau's layer and the next, so the plateau is the top layer drawn.
      level = plateau + LAYER_STEP / 2 - MOUND_TOP / layer.mesh.material.uniforms.uDepthOffset.value;
      updateMound(block.mask, block.proxy.box, block.rect);
      // Kept from the last success when the grown box cannot be mapped.
      mound.map = SW.planeMap(camera, renderer, mound.box, block.text.topZ) ?? mound.map;
      if (!ink) buildInk();
      drawInk();
      return true;
    }

    // True when the camera's view or projection moved past CAMERA_EPSILON (or
    // the viewport resized) since the last time this returned true. The camera
    // drifts by tiny amounts at rest; those do not trigger a re-place.
    function cameraMoved(camera) {
      const a = camera.matrixWorld.elements;
      const p = camera.projectionMatrix.elements;
      let moved = seen[32] !== innerWidth || seen[33] !== innerHeight;
      for (let i = 0; i < 16 && !moved; i++) {
        if (Math.abs(seen[i] - a[i]) > CAMERA_EPSILON || Math.abs(seen[16 + i] - p[i]) > CAMERA_EPSILON * (1 + Math.abs(p[i]))) moved = true;
      }
      if (!moved) return false;
      for (let i = 0; i < 16; i++) {
        seen[i] = a[i];
        seen[16 + i] = p[i];
      }
      seen[32] = innerWidth;
      seen[33] = innerHeight;
      return true;
    }

    function shownAmount() {
      let min = 1;
      for (const e of chain) {
        const css = getComputedStyle(e);
        if (css.visibility === "hidden" || css.display === "none") return 0;
        const o = parseFloat(css.opacity);
        if (o < min) min = o;
      }
      return min > 0 ? min : 0;
    }

    const stop = SW.onFrame(({ renderer, camera, dt }) => {
      try {
        title ||= document.querySelector("h2.sw-block-title");
        if (!title) return;
        if (!words) build();
        const shown = shownAmount();
        const moved = cameraMoved(camera);
        frame++;
        if (shown >= PLACE_FROM && (moved || !placed || frame % 30 === 0)) {
          const result = placeAll(camera, renderer);
          if (result !== null) ok = result;
          if (result) placed = true;
        }
        const target = placed && ok ? shown : 0;
        // The letters cannot stand: the DOM title shows instead.
        const flat = drawn && shown > 0 && !ok;
        if (flat !== isFlat) {
          isFlat = flat;
          document.documentElement.classList.toggle("sw-block-flat", flat);
        }
        rise += (target - rise) * Math.min(1, dt * SPEED);
        if (Math.abs(target - rise) < 0.001) rise = target;
        const eased = rise * rise * (3 - 2 * rise);
        for (const w of words) w.text.setAmount(eased);
        if (ink) {
          const t = Math.min(1, Math.max(0, (eased - INK_FROM) / (1 - INK_FROM)));
          const shownInk = target > 0 ? Math.round(t * t * (3 - 2 * t) * 100) / 100 : 0;
          if (shownInk !== inkShown) {
            inkShown = shownInk;
            ink.style.opacity = String(shownInk);
          }
        }
        if (placed && mound.map && (eased !== moundStrength || mound.map !== moundMap || level !== moundLevel)) {
          moundStrength = eased;
          moundMap = mound.map;
          moundLevel = level;
          SW.setMound(owner, { mask: mound, map: mound.map, strength: eased, level, lod: MOUND_LOD, gain: MOUND_GAIN });
        }
        if (placed && !drawn) {
          drawn = true;
          document.documentElement.classList.add("sw-block-3d");
        }
      } catch (err) {
        // Put the page back to the DOM title; SWText logs the error.
        stop();
        document.documentElement.classList.remove("sw-block-3d", "sw-block-flat");
        ink?.remove();
        SW.clearMound(owner);
        for (const w of words ?? []) w.text.dispose();
        mound.texture.dispose();
        throw err;
      }
    });
  });
})();
