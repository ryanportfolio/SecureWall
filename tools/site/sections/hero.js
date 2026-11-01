// Hero section: "Choose what connects" as extruded 3D letters rising out of
// the terrain (concept 01): white faces over tall rainbow-striped walls, ringed
// by terraced contour lines that follow the letter outlines and fade into the
// site's own terrain, which is left as it is.
//
// Meshes: sw-hero (the letters, a SWText.SliceText) and sw-hero-rings (the
// terraces, built here), the rings drawn from a distance field of the glyph
// mask.
//
// Built with window.SWText (tools/site/lib/swtext.js). Letters and rings
// rise out of the ground on load and fade with the hero's computed opacity,
// gone at SHOWN_FROM; gone, they sink, and they rise again when it comes
// back. While they stand they are placed onto the
// h1's box whenever it or the camera moves, so they stay on the DOM layout
// (clear of the tagline). As soon as the section is set up, <html> gets
// sw-hero-3d and hero.css makes the DOM h1 transparent, so the flat headline
// never shows first; it stays in the accessibility tree and selectable. The letters are
// first drawn once the headline font has loaded, so their strokes don't
// change weight when it arrives.
(() => {
  "use strict";
  const SW = window.SWText;
  if (!SW) return;

  // Heights are mesh-local z. The top terrain layer sits at 0.95 and the
  // camera about 0.23 above it, so walls look long: every height above the
  // rings' base is scaled by the font size over FONT_REF, which keeps the
  // walls in proportion to the letters on a phone.
  const FONT_REF = 190;
  // Contour rings around the letters: RINGS terraces, each RING_W font sizes
  // wide and RING_DZ high, the outermost just above the top terrain layer.
  const RINGS = 8;
  const RING_W = 0.07;
  const RING_DZ = 0.002;
  const RING_SLICES = 3;
  const RING_BASE = 0.9515;
  // The innermost terrace is mid-grey (RING_NEAR, linear), so the white faces
  // stand out from the ground, as in concept 01; the terraces lighten
  // outward to RING_FAR, close to the terrain's own top. Each darkens to
  // RING_EDGE toward its outer edge (the contour line), less so farther out.
  // Past RING_FADE of the way out they fade to RING_ALPHA opacity, so the
  // block has no outline of its own over the terrain.
  const RING_NEAR = 0.3;
  const RING_FAR = 0.6;
  const RING_EDGE = 0.7;
  const RING_FADE = 0.4;
  const RING_ALPHA = 0.15;
  // Letters: the face stands WALL above the innermost ring; the stack goes
  // down below the rings' base so no gap shows under the lowest ring. SLICES
  // keeps the step between slices near one pixel at 1440 px, so the wall
  // reads as one striped side; SHADE darkens wall parts facing away from the
  // light.
  const WALL = 0.045;
  const SLICES = 64;
  const SHADE = 1;
  // Small letters lose their white faces to the post-processing's colour
  // fringes and grain, so the face colour rises above 1 (the scene renders
  // to a float target) by up to FACE_GAIN as the font shrinks from FONT_REF.
  const FACE_GAIN = 0.8;
  // The site's chromatic aberration splits the outer letters into coloured
  // copies (it grows toward the screen's edges); SWText.lens scales it by
  // 1 - LENS on the glyphs, back to full LENS_REACH font sizes away.
  const LENS = 0.95;
  const LENS_REACH = 0.5;
  const FIELD_W = 384;
  // The glyph mask's edge blur (font sizes), which also softens the faces'
  // bevel. At 0.05 the blurs of two close glyphs summed past the mask's
  // cut-off in narrow gaps and drew a speck of letter between them.
  const MASK_EDGE = 0.03;
  // The site's headline face is a light weight; concept 01's letters are
  // heavy, so the mask widens every stroke by BOLD font sizes on each side
  // (hero.css spaces the letters out to keep them apart).
  const BOLD = 0.035;
  // Rise and sink rates per second (the first rise, on load, at
  // FIRST_SPEED). Only the top few percent of the travel shows above the
  // ground (on load the terrain is still flat at its top layer), so the
  // height (lift) covers the first 1 - BAND of it in the first DASH of the
  // time and eases out (cubic) over the rest.
  const FIRST_SPEED = 1;
  const RISE_SPEED = 4;
  const SINK_SPEED = 3;
  const BAND = 0.08;
  const DASH = 0.3;
  // Letters and rings fade with the hero as the DOM heading did: opaque
  // while its opacity is above FULL_FROM, gone at SHOWN_FROM, in proportion
  // between, so their opacity follows the scroll position. Gone, they sink
  // unseen. They also fade in at FADE_SPEED per second once the rise
  // passes FADE_FROM, the slow end where they come up through the ground,
  // so they never appear in one frame (coming back, the rise is quick, so
  // they follow the hero without a gap); leaving, they fade out at the same
  // rate and then sink unseen. On load the rings also wait for the terrain
  // to form: its uHeight rises from about 0.27 to 0.43, and they fade in
  // over TERRAIN_FROM..TERRAIN_TO, so they never sit on the blank intro
  // ground.
  const FADE_FROM = 0.35;
  const FADE_SPEED = 4;
  const SHOWN_FROM = 0.08;
  const FULL_FROM = 0.9;
  const TERRAIN_FROM = 0.3;
  const TERRAIN_TO = 0.38;
  const clamp01 = x => Math.min(1, Math.max(0, x));
  const lift = x => {
    x = Math.min(1, Math.max(0, x));
    if (x < DASH) return (1 - BAND) * (x / DASH);
    return 1 - BAND * (1 - (x - DASH) / (1 - DASH)) ** 3;
  };
  const FONT_WAIT = 3000;
  const CAMERA_STILL = 0.01;
  // The site slides the h1's lines in on load, redrawing the mask on every
  // frame for about a second; the ring field (about 20-60 ms to build) is
  // rebuilt once the mask has held still for FIELD_SETTLE ms.
  const FIELD_SETTLE = 150;

  // Exact squared Euclidean distance transform (Felzenszwalb), one line.
  function edt1(f, n, d, v, z) {
    let k = 0;
    v[0] = 0;
    z[0] = -Infinity;
    z[1] = Infinity;
    for (let q = 1; q < n; q++) {
      let s;
      while ((s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k])) <= z[k]) k--;
      k++;
      v[k] = q;
      z[k] = s;
      z[k + 1] = Infinity;
    }
    k = 0;
    for (let q = 0; q < n; q++) {
      while (z[k + 1] < q) k++;
      d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
    }
  }

  // Distance from the glyphs, in font sizes, over mask.box, in the red
  // channel (0..DIST_MAX) of `dist`, for the rings. It is taken from the
  // glyphs closed with radius CLOSE (font sizes), so no terrace sits in the
  // narrow gaps between letters, and outside them it is box-blurred twice
  // over BLUR font sizes, which rounds the sharp notches where the contours
  // of neighbouring letters meet. DIST_MAX covers the rings (RINGS x RING_W)
  // and the lens reach; kept small, its 8-bit steps stay under a pixel.
  const DIST_MAX = 0.8;
  const BLUR = 0.12;
  const CLOSE = 0.1;

  // In place, along rows (step 1) or columns (step w): mean over 2r + 1 cells.
  function boxBlur(a, w, h, r, along) {
    const n = along === "x" ? w : h, lines = along === "x" ? h : w;
    const step = along === "x" ? 1 : w, next = along === "x" ? w : 1;
    const line = new Float64Array(n);
    for (let l = 0; l < lines; l++) {
      const o = l * next;
      for (let i = 0; i < n; i++) line[i] = a[o + i * step];
      let sum = 0;
      for (let i = -r; i <= r; i++) sum += line[Math.min(n - 1, Math.max(0, i))];
      for (let i = 0; i < n; i++) {
        a[o + i * step] = sum / (2 * r + 1);
        sum += line[Math.min(n - 1, i + r + 1)] - line[Math.max(0, i - r)];
      }
    }
  }

  // In place: each cell of grid (0 for seed cells, 1e12 elsewhere) becomes
  // its squared distance in cells to the nearest seed.
  function edt2(grid, w, h) {
    const len = Math.max(w, h);
    const f = new Float64Array(len), d = new Float64Array(len), z = new Float64Array(len + 1);
    const v = new Int32Array(len);
    for (let x = 0; x < w; x++) {
      for (let y = 0; y < h; y++) f[y] = grid[y * w + x];
      edt1(f, h, d, v, z);
      for (let y = 0; y < h; y++) grid[y * w + x] = d[y];
    }
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) f[x] = grid[y * w + x];
      edt1(f, w, d, v, z);
      for (let x = 0; x < w; x++) grid[y * w + x] = d[x];
    }
    return grid;
  }

  // Squared distance to the morphological closing (radius r cells) of the
  // seed set whose squared distances grid holds: concave corners of the
  // outline are rounded to radius r, convex ones are kept. Cells beyond the
  // grid count as outside.
  function close(grid, w, h, r) {
    const p = Math.ceil(r) + 1;
    const W = w + 2 * p, H = h + 2 * p;
    const g = new Float64Array(W * H);
    const r2 = r * r;
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const gx = x - p, gy = y - p;
        const inside = gx >= 0 && gy >= 0 && gx < w && gy < h;
        // Seeds: cells farther than r from the glyphs (the outside of the dilation).
        g[y * W + x] = !inside || grid[gy * w + gx] > r2 ? 0 : 1e12;
      }
    }
    edt2(g, W, H);
    // The closing: cells at least r from that outside.
    for (let i = 0; i < W * H; i++) g[i] = g[i] >= r2 ? 0 : 1e12;
    edt2(g, W, H);
    const out = new Float64Array(w * h);
    for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) out[y * w + x] = g[(y + p) * W + x + p];
    return out;
  }

  // The field as RGBA pixels (distance in red) from the mask's RGBA pixels
  // (w x h): squared distances to the glyphs (alpha >= 0.5) and to their
  // closing, the latter blurred; scale converts cells to font sizes. Pure,
  // so it also runs in the worker below.
  function fieldPixels(mask, w, h, scale, { close: radius, blur, max }) {
    const n = w * h;
    const raw = new Float64Array(n);
    for (let i = 0; i < n; i++) raw[i] = mask[i * 4 + 3] >= 128 ? 0 : 1e12;
    edt2(raw, w, h);
    const grid = close(raw, w, h, radius / scale);
    const d = new Float64Array(n);
    for (let i = 0; i < n; i++) d[i] = Math.sqrt(grid[i]) * scale;
    const r = Math.max(1, Math.round(blur / scale));
    for (let k = 0; k < 2; k++) {
      boxBlur(d, w, h, r, "x");
      boxBlur(d, w, h, r, "y");
    }
    const out = new Uint8ClampedArray(n * 4);
    for (let i = 0; i < n; i++) {
      out[i * 4] = raw[i] ? Math.max(1, Math.round(Math.min(1, d[i] / max) * 255)) : 0;
      out[i * 4 + 3] = 255;
    }
    return out;
  }

  // Builds fields off the main thread when the browser can (reading the
  // mask back and the transforms took about 60 ms, a dropped frame or
  // three): the mask is scaled down to the grid by createImageBitmap and
  // read and processed in a worker made from the functions above.
  const WORKER = [edt1, edt2, close, boxBlur, fieldPixels].map(String).join("\n") +
    `
onmessage = ({ data: { image, w, h, scale, opts } }) => {
  const ctx = new OffscreenCanvas(w, h).getContext("2d");
  ctx.drawImage(image, 0, 0, w, h);
  image.close();
  const px = fieldPixels(ctx.getImageData(0, 0, w, h).data, w, h, scale, opts);
  postMessage(px, [px.buffer]);
};`;
  let worker;
  function fieldWorker() {
    if (worker === undefined) {
      worker = null;
      try {
        if (typeof OffscreenCanvas === "function") {
          const url = URL.createObjectURL(new Blob([WORKER], { type: "text/javascript" }));
          worker = new Worker(url);
          URL.revokeObjectURL(url);
        }
      } catch {
        worker = null;
      }
    }
    return worker;
  }

  function fields(T) {
    const dist = { canvas: document.createElement("canvas"), texture: null };
    const scratch = document.createElement("canvas");
    const opts = { close: CLOSE, blur: BLUR, max: DIST_MAX };

    // Rebuilds the field from the mask's current drawing; resolves once
    // done (one build at a time).
    async function update(mask, fontSize) {
      const box = mask.box;
      const w = FIELD_W;
      const h = Math.max(1, Math.round((w * box.height) / box.width));
      const scale = box.width / w / fontSize; // cells -> font sizes
      const small = () => createImageBitmap(mask.canvas, { resizeWidth: w, resizeHeight: h, resizeQuality: "medium" });
      let px = null;
      const wk = fieldWorker();
      if (wk) {
        const image = await small();
        // A worker that fails is dropped for good, and this build runs here.
        px = await new Promise(resolve => {
          wk.onmessage = e => resolve(e.data);
          wk.onerror = e => {
            e.preventDefault();
            wk.terminate();
            worker = null;
            resolve(null);
          };
          wk.postMessage({ image, w, h, scale, opts }, [image]);
        });
      }
      if (!px) {
        const image = await small();
        scratch.width = w;
        scratch.height = h;
        const ctx = scratch.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(image, 0, 0, w, h);
        image.close();
        px = fieldPixels(ctx.getImageData(0, 0, w, h).data, w, h, scale, opts);
      }
      const { canvas } = dist;
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
        dist.texture?.dispose();
        dist.texture = null;
      }
      canvas.getContext("2d").putImageData(new ImageData(px, w, h), 0, 0);
      dist.texture ||= new T.Texture(canvas);
      dist.texture.needsUpdate = true;
    }
    return { dist, update };
  }

  const RING_VERTEX = `varying vec3 vLocal;varying float vSlice;
void main(){vec4 p=vec4(position,1.);
#ifdef USE_INSTANCING
p=instanceMatrix*p;
#endif
vLocal=p.xyz;vSlice=float(gl_InstanceID);gl_Position=projectionMatrix*modelViewMatrix*p;}`;

  // Instance i is slice (i % per) of ring RINGS - 1 - floor(i / per), ring 0
  // innermost, so the outer rings draw first and each inner ring's
  // antialiased edge blends over the ring below it.
  // Inside the glyphs nothing is drawn, so sinking letters leave the plain
  // terrain behind instead of letter-shaped grey pits.
  // Slice 0 of each ring is its flat top, shaded darker toward its outer
  // edge like the terrain's own steps; the rest are its wall, darkening
  // downward.
  const RING_FRAGMENT = `uniform sampler2D tDist;uniform mat3 uMap;uniform float uWidth;uniform float uPer;uniform float uRings;
uniform float uDistMax;uniform vec3 uFace;uniform vec3 uFar;uniform float uEdge;uniform float uFadeFrom;uniform float uAlpha;uniform float uOpacity;
varying vec3 vLocal;varying float vSlice;
void main(){vec3 p=uMap*vec3(vLocal.xy/5.+.5,1.);if(p.z<=0.)discard;vec2 t=p.xy/p.z;
if(t.x<0.||t.x>1.||t.y<0.||t.y>1.)discard;
float d=texture2D(tDist,t).r*uDistMax;
float k=floor(vSlice/uPer+.001);float ring=uRings-1.-k;float sub=vSlice-k*uPer;
float edge=(ring+1.)*uWidth;
float aa=fwidth(d);if(d>edge+aa||d<=0.)discard;
float o=ring/max(uRings-1.,1.);vec3 face=mix(uFace,uFar,o);
vec3 c;
if(sub<.5)c=face*mix(mix(uEdge,1.,o),1.,smoothstep(0.,.55,clamp((edge-d)/uWidth,0.,1.)));
else c=face*mix(.95,.7,(sub-1.)/max(uPer-1.,1.));
gl_FragColor=vec4(c,mix(1.,uAlpha,smoothstep(uFadeFrom,1.,o))*uOpacity*(1.-smoothstep(edge-aa,edge+aa,d)));
#include <colorspace_fragment>
}`;

  // Inverse of a column-major mat3.
  function inv3(m) {
    const [a, b, c, d, e, f, g, h, i] = m;
    const A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
    const det = a * A + b * B + c * C;
    if (!det) return null;
    return [A, -(b * i - c * h), b * f - c * e, B, a * i - c * g, -(a * f - c * d), C, -(a * h - b * g), a * e - b * d].map(x => x / det);
  }

  class Rings {
    constructor(layer, dist, dz) {
      const T = SW.three(layer);
      this.dist = dist;
      this.dz = dz;
      this.per = Math.max(1, RING_SLICES);
      this.count = RINGS * this.per;
      this.amount = 0;
      this.footprint = null;
      this._m = new T.Matrix4();
      this.uniforms = {
        tDist: { value: null },
        uMap: { value: [1, 0, 0, 0, 1, 0, 0, 0, 1] },
        uWidth: { value: RING_W },
        uPer: { value: this.per },
        uDistMax: { value: DIST_MAX },
        uFace: { value: [RING_NEAR, RING_NEAR, RING_NEAR] },
        uFar: { value: [RING_FAR, RING_FAR, RING_FAR] },
        uRings: { value: RINGS },
        uEdge: { value: RING_EDGE },
        uFadeFrom: { value: RING_FADE },
        uAlpha: { value: RING_ALPHA },
        uOpacity: { value: 1 },
      };
      const material = new T.ShaderMaterial({
        vertexShader: RING_VERTEX,
        fragmentShader: RING_FRAGMENT,
        uniforms: this.uniforms,
        transparent: true,
        depthTest: true,
        depthWrite: true,
      });
      const mesh = (this.mesh = new T.InstancedMesh(new T.PlaneGeometry(1, 1), material, this.count));
      mesh.name = "sw-hero-rings";
      mesh.frustumCulled = false;
      // The terrain is drawn with the transparent objects too; the rings
      // blend over it, so they go after it.
      mesh.renderOrder = 1;
      mesh.visible = false;
      mesh.position.copy(layer.mesh.position);
      mesh.quaternion.copy(layer.mesh.quaternion);
      mesh.scale.copy(layer.mesh.scale);
      mesh.onBeforeRender = () => {
        this.uniforms.tDist.value = this.dist.texture;
      };
      layer.add(mesh);
    }

    // Same footprint as the letters: map takes terrain uv to the mask box.
    place(map) {
      const inv = inv3(map);
      if (!inv) return;
      const xs = [], ys = [];
      for (const [s, t] of [[0, 0], [1, 0], [1, 1], [0, 1]]) {
        const X = inv[0] * s + inv[3] * t + inv[6];
        const Y = inv[1] * s + inv[4] * t + inv[7];
        const W = inv[2] * s + inv[5] * t + inv[8];
        xs.push((X / W - 0.5) * 5);
        ys.push((Y / W - 0.5) * 5);
      }
      const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
      this.footprint = [(x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0];
      this.uniforms.uMap.value = map;
      this._write();
    }

    setOpacity(o) {
      this.uniforms.uOpacity.value = o;
    }

    setAmount(a) {
      if (a === this.amount) return;
      this.amount = a;
      this._write();
    }

    _write() {
      const mesh = this.mesh;
      mesh.visible = !!this.footprint && this.amount > 0.002;
      if (!this.footprint) return;
      const [cx, cy, sx, sy] = this.footprint;
      const dz = this.dz;
      const top = RING_BASE + (RINGS - 1) * dz;
      // Sinks until the top ring is below the lowest terrain layer (z 0).
      const drop = (1 - this.amount) * (top + 0.05);
      const step = dz / this.per;
      for (let i = 0; i < this.count; i++) {
        const ring = RINGS - 1 - Math.floor(i / this.per);
        const sub = i % this.per;
        const z = RING_BASE + (RINGS - 1 - ring) * dz - sub * step - drop;
        this._m.makeScale(sx, sy, 1).setPosition(cx, cy, z);
        mesh.setMatrixAt(i, this._m);
      }
      mesh.instanceMatrix.needsUpdate = true;
    }

    dispose() {
      const mesh = this.mesh;
      mesh.parent?.remove(mesh);
      mesh.geometry.dispose();
      mesh.material.dispose();
      mesh.dispose?.();
    }
  }

  // Adds the class that makes the DOM headline transparent, at once: the
  // stylesheet's color transition is for handing the page back to the DOM
  // headline, not for hiding it.
  function hideHeading(el, cls) {
    const old = el.style.transition;
    el.style.transition = "none";
    document.documentElement.classList.add(cls);
    getComputedStyle(el).color; // applies the class before the transition returns
    el.style.transition = old;
  }

  SW.onTerrain(layer => {
    let mask = null;
    let field = null;
    let text = null;
    let rings = null;
    let built = 0; // font size the meshes were built for
    let topZ = 0;
    let version = -1; // mask texture version the field was built from
    let source = null;
    let fieldStale = false; // the mask changed since the field was built
    let fieldBusy = false; // a build is under way
    let maskChanged = 0; // when it last changed (ms)
    let placed = false;
    let placedFor = null; // camera matrices and h1 box of the last placement
    let stop = null;
    let stopFooter = null;
    let rise = 0;
    let settled = false; // the load intro is over
    let risen = false; // the first rise is done
    let appear = 0; // eases the letters in and out as they rise and leave
    let lensIn = 0; // eases the lens in once the field is first built
    const lensOptions = { texture: null, rect: null, strength: 0, reach: LENS_REACH / DIST_MAX };
    let formed = 0; // how far the terrain has formed on load (1: for good)
    let lastCamera = null;
    let hero = null;
    let title = null;

    function build(fontSize) {
      text?.dispose();
      rings?.dispose();
      const s = Math.min(1, fontSize / FONT_REF);
      const dz = RING_DZ * s;
      const face = 1 + FACE_GAIN * (1 - s);
      topZ = RING_BASE + (RINGS - 1) * dz + WALL * s;
      text = new SW.SliceText(layer, {
        mask,
        slices: SLICES,
        baseZ: RING_BASE - 0.005,
        topZ,
        face: [face, face, face],
        sides: "rainbow",
        stripe: 2,
        bevel: 1.8,
        shade: SHADE,
        blend: true,
      });
      text.mesh.name = "sw-hero";
      rings = new Rings(layer, field.dist, dz);
      built = fontSize;
    }

    // Finds the headline and hides the DOM one; false while it is not in the
    // page yet (onFrame tries again).
    function setup() {
      if (title) return true;
      hero ||= document.querySelector(".js-hero");
      title = hero?.querySelector("h1") ?? null;
      if (!title) return false;
      hideHeading(title, "sw-hero-3d");
      return true;
    }

    // Puts the page back to the DOM headline; SWText logs the error.
    function fail(err) {
      stop?.();
      stopFooter?.();
      document.documentElement.classList.remove("sw-hero-3d");
      SW.lens();
      text?.dispose();
      rings?.dispose();
      throw err;
    }

    stop = SW.onFrame(({ renderer, camera, dt, now }) => {
      try {
        if (!setup()) return;
        if (!mask) {
          // Waits for the headline font, for at most FONT_WAIT ms after the
          // page started (a font that never loads leaves the fallback face).
          const cs = getComputedStyle(title);
          if (document.fonts && !document.fonts.check(`${cs.fontStyle} ${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`) && performance.now() < FONT_WAIT) return;
          mask = SW.glyphMask(title, { pad: 0.2, edge: MASK_EDGE, bold: BOLD });
          field = fields(SW.three(layer));
        }
        const css = getComputedStyle(hero);
        const shown = css.visibility === "hidden" ? 0 : parseFloat(css.opacity) || 0;
        if ((shown > SHOWN_FROM || rise > 0) && mask.update()) {
          // The mask redraws when the glyphs move or the font changes; only
          // then rebuild the field (and the meshes, for a new font size).
          if (mask.texture !== source || mask.texture.version !== version) {
            source = mask.texture;
            version = source.version;
            const fontSize = parseFloat(getComputedStyle(title).fontSize);
            if (fontSize !== built) build(fontSize);
            fieldStale = true;
            maskChanged = now;
            placedFor = null;
          }
          if (fieldStale && !fieldBusy && settled && now - maskChanged >= FIELD_SETTLE) {
            fieldStale = false;
            fieldBusy = true;
            field.update(mask, built).catch(fail).finally(() => (fieldBusy = false));
          }
          // Placing rewrites both meshes' instance matrices; it is skipped
          // while the mask, the camera and the h1's box hold still.
          const e = camera.matrixWorld.elements, q = camera.projectionMatrix.elements, b = mask.box;
          let moved = !placedFor;
          for (let i = 0; !moved && i < 16; i++) moved = e[i] !== placedFor[i] || q[i] !== placedFor[16 + i];
          if (!moved) moved = b.left !== placedFor[32] || b.top !== placedFor[33] || b.width !== placedFor[34] || b.height !== placedFor[35];
          if (moved && text.place(camera, renderer)) {
            placed = true;
            placedFor ||= new Float64Array(36);
            placedFor.set(e);
            placedFor.set(q, 16);
            placedFor.set([b.left, b.top, b.width, b.height], 32);
            rings.place(text.map);
          }
        }
        if (!text) return;
        // The site fades its canvas in after loading and swings the camera
        // in; the letters first rise once the canvas is half shown and the
        // camera has settled (moving less than CAMERA_STILL a frame), so
        // the rise is seen and the letters don't ride the swing.
        if (!settled) {
          const e = camera.matrixWorld.elements;
          let moved = lastCamera ? 0 : Infinity;
          for (let i = 0; lastCamera && i < 16; i++) moved = Math.max(moved, Math.abs(e[i] - lastCamera[i]));
          lastCamera = e.slice();
          const canvasShown = parseFloat(getComputedStyle(renderer.domElement).opacity);
          settled = placed && !(canvasShown < 0.5) && moved < CAMERA_STILL;
        }
        const stand = settled && shown > SHOWN_FROM;
        const target = stand || appear > 0 ? 1 : 0;
        const speed = target < rise ? SINK_SPEED : risen ? RISE_SPEED : FIRST_SPEED;
        rise += Math.max(-dt * speed, Math.min(dt * speed, target - rise));
        const show = stand && rise >= FADE_FROM ? 1 : 0;
        appear += Math.max(-dt * FADE_SPEED, Math.min(dt * FADE_SPEED, show - appear));
        if (rise >= 1) risen = true;
        const fade = appear * clamp01((shown - SHOWN_FROM) / (FULL_FROM - SHOWN_FROM));
        if (formed < 1) {
          const h = layer.mesh.material.uniforms.uHeight?.value;
          formed = typeof h === "number" ? clamp01((h - TERRAIN_FROM) / (TERRAIN_TO - TERRAIN_FROM)) : 1;
        }
        const amount = lift(rise);
        if (amount !== text.amount) text.setAmount(amount);
        text.setFade(fade);
        rings.setAmount(amount);
        // Squared, so mid-fade the rings' outlines don't show through the letters
        // as copies of them.
        rings.setOpacity(fade * fade * formed);
        lensOptions.texture = field.dist.texture;
        lensOptions.rect = mask.box;
        // The lens eases in once the field exists (it is built a moment after
        // the letters rise), so the fringes don't vanish in one frame.
        if (field.dist.texture) lensIn = Math.min(1, lensIn + dt * FADE_SPEED);
        lensOptions.strength = placed ? LENS * fade * lensIn : 0;
        SW.lens(lensOptions);
      } catch (err) {
        fail(err);
      }
    });
    // Hero frames stop at the end of the WebGL area, possibly before the
    // letters have sunk (a fast jump down the page). The footer renders the
    // same scene, so drop them on its first frame.
    stopFooter = SW.onFrame(() => {
      if (!rise) return;
      rise = 0;
      appear = 0;
      text?.setAmount(0);
      rings?.setAmount(0);
      SW.lens();
    }, { name: "Layers Footer" });
    try {
      setup();
    } catch (err) {
      fail(err);
    }
  });
})();
