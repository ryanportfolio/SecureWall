// Hero section: "Choose what connects" as extruded 3D letters rising out of
// the terrain (concept 01): white faces over rainbow-striped walls, ringed by
// terraced contour lines that follow the letter outlines, on a mound that
// raises the terrain to meet the rings so its own contour lines bend around
// the block.
//
// Meshes: sw-hero (the letters, a SWText.SliceText) and sw-hero-rings (the
// terraces, built here). Both are drawn from a distance field of the glyph
// mask, which also shapes the mound.
//
// Built with window.SWText (tools/site/lib/swtext.js). The letters follow the
// hero's computed opacity: they rise while it shows and sink into the terrain
// as it fades on scroll. They are placed onto the h1's box only while the hero
// is fully shown, so while moving they travel with the terrain. As soon as the
// section is set up, <html> gets sw-hero-3d and hero.css makes the DOM h1
// transparent, so the flat headline never shows first; it stays in the
// accessibility tree and selectable.
(() => {
  "use strict";
  const SW = window.SWText;
  if (!SW) return;

  // Heights are mesh-local z. The top terrain layer sits at 0.95 and the
  // camera about 0.23 above it, so walls look long: every height above the
  // rings' base is scaled by the font size over FONT_REF, which keeps the
  // walls in proportion to the letters on a phone.
  const FONT_REF = 190;
  const MOUND_LEVEL = 0.95;
  // Contour rings around the letters: RINGS terraces, each RING_W font sizes
  // wide and RING_DZ high, the outermost just above the top terrain layer.
  const RINGS = 7;
  const RING_W = 0.08;
  const RING_DZ = 0.002;
  const RING_SLICES = 3;
  const RING_BASE = 0.9515;
  const RING_FACE = [0.6, 0.6, 0.6];
  // Each terrace darkens to RING_EDGE toward its outer edge (the contour
  // line); its wall mixes RING_TINT of the letters' stripe colors.
  const RING_EDGE = 0.7;
  const RING_TINT = 0.6;
  // Letters: the face stands WALL above the innermost ring; the stack goes
  // down below the rings' base so no gap shows under the lowest ring.
  const WALL = 0.024;
  const SLICES = 30;
  // Small letters lose their white faces to the post-processing's colour
  // fringes and grain, so the face colour rises above 1 (the scene renders
  // to a float target) by up to FACE_GAIN as the font shrinks from FONT_REF.
  const FACE_GAIN = 0.8;
  // Beyond the rings the terrain falls back over MOUND_R font sizes.
  const MOUND_R = 1;
  // The mound is shaped around the glyphs closed with this radius (font
  // sizes), so its outline has no tight inside corners. In such a corner the
  // terrace just below the mound's top showed through as a sliver, and the
  // mound's own shadow made it a dark triangle or notch.
  const CLOSE = 1.5;
  const FIELD_W = 512;
  // Rise/sink speed (per second) toward the hero's opacity.
  const SPEED = 3.5;

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

  // Distance from the glyphs, in font sizes. `dist` covers mask.box and
  // holds it in its red channel (0..DIST_MAX) for the rings. `mound` holds
  // the mound height in alpha for SWText.setMound over moundRect(), a larger
  // rect, so the slope ends inside it instead of at the box edge. The rect
  // stays inside the viewport: points far above it map badly onto the plane.
  // So it can change while the mask does not (a resize that keeps the
  // headline's layout); the mound is redrawn whenever its rect, relative to
  // mask.box, differs from the one it was drawn for (rectKey).
  const DIST_MAX = 2;
  const FLAT = RINGS * RING_W;
  // The mound fades out over the outer 6% of its rect; keep the slope clear.
  const moundMargin = fontSize => (FLAT + MOUND_R) * fontSize;
  function moundRect(box, fontSize) {
    const m = moundMargin(fontSize);
    const w = box.width + 2 * m, h = box.height + 2 * m;
    const e = 0.08 * Math.max(w, h);
    const x0 = Math.max(0, box.left - m - e), y0 = Math.max(0, box.top - m - e);
    const x1 = Math.min(innerWidth, box.right + m + e), y1 = Math.min(innerHeight, box.bottom + m + e);
    return new DOMRect(x0, y0, x1 - x0, y1 - y0);
  }
  const rectKey = (box, rect) =>
    [rect.left - box.left, rect.top - box.top, rect.width, rect.height].map(Math.round).join();

  // Squared distance in grid cells to the nearest cell inside the glyphs
  // (alpha >= 0.5), for a w x h grid with the mask drawn at (dx, dy, dw, dh).
  function distanceGrid(maskCanvas, w, h, dx, dy, dw, dh, scratch) {
    scratch.width = w;
    scratch.height = h;
    const ctx = scratch.getContext("2d", { willReadFrequently: true });
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(maskCanvas, dx, dy, dw, dh);
    const alpha = ctx.getImageData(0, 0, w, h).data;
    const n = w * h;
    const grid = new Float64Array(n);
    for (let i = 0; i < n; i++) grid[i] = alpha[i * 4 + 3] >= 128 ? 0 : 1e12;
    return edt2(grid, w, h);
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

  function fields(T) {
    const make = () => ({ canvas: document.createElement("canvas"), texture: null });
    const dist = make();
    const mound = make();
    const scratch = document.createElement("canvas");

    function write(target, w, h, fill) {
      const { canvas } = target;
      if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
        target.texture?.dispose();
        target.texture = null;
      }
      const ctx = canvas.getContext("2d");
      const img = ctx.createImageData(w, h);
      fill(img.data);
      ctx.putImageData(img, 0, 0);
      target.texture ||= new T.Texture(canvas);
      target.texture.needsUpdate = true;
    }

    // Rebuilds both fields from the mask's current drawing, the mound over
    // rect (moundRect of the mask's box).
    function update(mask, fontSize, rect) {
      const box = mask.box;
      let w = FIELD_W;
      let h = Math.max(1, Math.round((w * box.height) / box.width));
      let grid = distanceGrid(mask.canvas, w, h, 0, 0, w, h, scratch);
      let scale = box.width / w / fontSize; // cells -> font sizes
      write(dist, w, h, data => {
        for (let i = 0; i < w * h; i++) {
          data[i * 4] = Math.round(Math.min(1, (Math.sqrt(grid[i]) * scale) / DIST_MAX) * 255);
          data[i * 4 + 3] = 255;
        }
      });

      w = FIELD_W;
      h = Math.max(1, Math.round((w * rect.height) / rect.width));
      const k = w / rect.width;
      grid = distanceGrid(mask.canvas, w, h, (box.left - rect.left) * k, (box.top - rect.top) * k, box.width * k, box.height * k, scratch);
      grid = close(grid, w, h, CLOSE * fontSize * k);
      scale = 1 / k / fontSize;
      write(mound, w, h, data => {
        for (let i = 0; i < w * h; i++) {
          const t = 1 - Math.min(1, Math.max(0, Math.sqrt(grid[i]) * scale - FLAT) / MOUND_R);
          // The terrain eases the mound with smoothstep; invert it so the
          // height falls linearly with distance.
          const a = 0.5 - Math.sin(Math.asin(1 - 2 * t) / 3);
          data[i * 4] = data[i * 4 + 1] = data[i * 4 + 2] = 255;
          data[i * 4 + 3] = Math.round(a * 255);
        }
      });
    }
    return { dist, mound, update };
  }

  const RING_VERTEX = `varying vec3 vLocal;varying float vSlice;
void main(){vec4 p=vec4(position,1.);
#ifdef USE_INSTANCING
p=instanceMatrix*p;
#endif
vLocal=p.xyz;vSlice=float(gl_InstanceID);gl_Position=projectionMatrix*modelViewMatrix*p;}`;

  // Instance i is slice (i % per) of ring floor(i / per), ring 0 innermost.
  // Slice 0 of each ring is its flat top, shaded darker toward its outer
  // edge like the terrain's own steps; the rest are its striped wall.
  const RING_FRAGMENT = `uniform sampler2D tDist;uniform mat3 uMap;uniform float uWidth;uniform float uPer;
uniform float uDistMax;uniform vec3 uFace;uniform float uEdge;uniform float uTint;
varying vec3 vLocal;varying float vSlice;
vec3 band(float i){float k=mod(i,6.);
if(k<.5)return vec3(1.,.42,.08);if(k<1.5)return vec3(1.,.82,.18);if(k<2.5)return vec3(.95,.95,.93);
if(k<3.5)return vec3(.18,.82,1.);if(k<4.5)return vec3(.52,.32,1.);return vec3(1.,.32,.66);}
void main(){vec3 p=uMap*vec3(vLocal.xy/5.+.5,1.);if(p.z<=0.)discard;vec2 t=p.xy/p.z;
if(t.x<0.||t.x>1.||t.y<0.||t.y>1.)discard;
float d=texture2D(tDist,t).r*uDistMax;
float ring=floor(vSlice/uPer+.001);float sub=vSlice-ring*uPer;
float edge=(ring+1.)*uWidth;
if(d>edge)discard;
vec3 c;
if(sub<.5){float k=clamp((edge-d)/uWidth,0.,1.);c=uFace*mix(uEdge,1.,smoothstep(0.,.55,k));}
else c=mix(uFace,band(sub-1.+ring*2.),uTint)*mix(.95,.7,(sub-1.)/max(uPer-1.,1.));
gl_FragColor=vec4(c,1.);
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
        uFace: { value: RING_FACE },
        uEdge: { value: RING_EDGE },
        uTint: { value: RING_TINT },
      };
      const material = new T.ShaderMaterial({
        vertexShader: RING_VERTEX,
        fragmentShader: RING_FRAGMENT,
        uniforms: this.uniforms,
        transparent: false,
        depthTest: true,
        depthWrite: true,
      });
      const mesh = (this.mesh = new T.InstancedMesh(new T.PlaneGeometry(1, 1), material, this.count));
      mesh.name = "sw-hero-rings";
      mesh.frustumCulled = false;
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
      const drop = (1 - this.amount) * (top + 0.05);
      const step = dz / this.per;
      for (let i = 0; i < this.count; i++) {
        const ring = Math.floor(i / this.per);
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
    let moundMap = null;
    let version = -1; // mask texture version the fields were built from
    let source = null;
    let drawnFor = ""; // rectKey the mound was drawn for
    let placed = false;
    let stop = null;
    let rise = 0;
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
      document.documentElement.classList.remove("sw-hero-3d");
      if (hero) SW.clearMound(hero);
      text?.dispose();
      rings?.dispose();
      throw err;
    }

    stop = SW.onFrame(({ renderer, camera, dt }) => {
      try {
        if (!setup()) return;
        if (!mask) {
          mask = SW.glyphMask(title, { pad: 0.2, edge: 0.05 });
          field = fields(SW.three(layer));
        }
        const css = getComputedStyle(hero);
        const shown = css.visibility === "hidden" ? 0 : parseFloat(css.opacity) || 0;
        if (shown > 0.99 && mask.update()) {
          // The mask redraws when the glyphs move or the font changes; only
          // then rebuild the fields (and the meshes, for a new font size),
          // and the mound also when its rect changed.
          let redraw = false;
          if (mask.texture !== source || mask.texture.version !== version) {
            source = mask.texture;
            version = source.version;
            const fontSize = parseFloat(getComputedStyle(title).fontSize);
            if (fontSize !== built) build(fontSize);
            redraw = true;
          }
          const rect = moundRect(mask.box, built);
          const key = rectKey(mask.box, rect);
          if (redraw || key !== drawnFor) {
            field.update(mask, built, rect);
            drawnFor = key;
          }
          const map = text.place(camera, renderer) && SW.planeMap(camera, renderer, rect, topZ);
          if (map) {
            placed = true;
            moundMap = map;
            rings.place(text.map);
          }
        }
        if (!text) return;
        const target = placed ? shown : 0;
        rise += (target - rise) * Math.min(1, dt * SPEED);
        const eased = rise * rise * (3 - 2 * rise);
        text.setAmount(eased);
        rings.setAmount(eased);
        if (placed) SW.setMound(hero, { mask: field.mound, map: moundMap, strength: eased, level: MOUND_LEVEL, lod: 0, gain: 1 });
      } catch (err) {
        fail(err);
      }
    });
    try {
      setup();
    } catch (err) {
      fail(err);
    }
  });
})();
