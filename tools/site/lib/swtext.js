// SWText: 3D headline text in the secwall.org terrain scene.
//
// patch-secwall-site.mjs serves this classic script as /build/sw-swtext-<hash>.js
// ahead of the section scripts (tools/site/sections/*.js) and the entry bundle,
// and patches the bundle to push each terrain instance onto window.__swLayers.
// Sections use only the API below. three.js is not loaded separately: its
// classes are taken from objects the site already built.
//
// Terrains: the site builds two terrain instances, "Layers Hero" (shown from
// the hero down to .js-webgl-scroll-end) and "Layers Footer" (shown in the
// footer, where the site starts WebGL again). Both are attached. Functions
// that pick a terrain take its name (onTerrain, onFrame) or its layer
// (planeMap, setMound; SliceText's first argument); left out, they mean
// "Layers Hero", so code written for the hero alone works unchanged.
//
// Space: "mesh-local" is the terrain mesh's local space. The terrain is 20
// PlaneGeometry(1,1) layers scaled 5 (x, y in -2.5..2.5), each raised to its
// layer offset in z (0.05..0.95; the 20th wraps to 0). Terrain uv is
// (x / 5 + 0.5, y / 5 + 0.5). Both terrain meshes' world matrices are
// identity. Colors are linear RGB in 0..1.
//
// API (window.SWText):
//
//   onTerrain(cb, { name = "Layers Hero" } = {})
//     Calls cb(layer) once for the terrain instance whose options.name is
//     name ("Layers Hero" or "Layers Footer"), whether it was pushed to
//     __swLayers before or after this call. layer.mesh is the terrain
//     InstancedMesh; layer is its parent Object3D. This library is the only
//     code that overrides __swLayers.push. If setting up that terrain
//     failed, cb is never called, so a section that does all its work inside
//     cb adds nothing. prefers-reduced-motion is not checked: the page gets
//     the same headlines either way.
//     Every other function below needs a terrain, so call them from inside
//     cb (or later).
//
//   three(layer) -> { Vector3, Matrix4, Texture, InstancedMesh, PlaneGeometry,
//                     ShaderMaterial, InstancedBufferAttribute }
//     Constructors taken from existing objects. Vector3 is the camera
//     position's constructor once a frame has rendered (before that, the mesh
//     position's, the same class).
//
//   onFrame(fn, { name = "Layers Hero" } = {}) -> unsubscribe()
//     fn({ renderer, camera, dt, now, layer }) runs once per frame from the
//     onBeforeRender of the terrain mesh named name (any existing handler
//     still runs first), so only on frames where that terrain renders; layer
//     is that terrain. The site shows the hero terrain from the hero down to
//     .js-webgl-scroll-end and the footer terrain in the footer, never both
//     at rest; frames stop where neither renders. A section's meshes added to
//     a terrain's layer still render while that terrain mesh is hidden, so a
//     section hides them itself when its frames stop. dt is in seconds since
//     that terrain's previous frame, capped at 0.1; now is performance.now().
//     The terrain is transparent, so it draws after opaque meshes: changes
//     made here to an opaque mesh (SliceText) show from the next frame, while
//     terrain uniforms (setMound) apply to this frame. A throwing fn is
//     unsubscribed.
//
//   glyphMask(element, { pad = 0.15, width = 2048, edge = 0.05 })
//     -> { canvas, texture, update(), box }
//     A canvas alpha mask of the element's text, each character drawn with the
//     element's computed font where the page lays it out (measured with Range
//     rects, so transforms on characters count). box is the padded client
//     rect (a DOMRect) the canvas covers: the element's rect grown on every
//     side by pad x the larger of its width and height, so blurred lookups are
//     not clipped. The canvas keeps the box's aspect, at most width px wide
//     and 2048 px tall. edge is the blur radius as a fraction of the font
//     size: alpha 0.5 sits on the glyph outline. update() re-measures, moves
//     box to the element's current rect, redraws only when a glyph position
//     (relative to the box), the box size or the font changed, and returns
//     true when the mask is valid (false while the element has no size).
//     texture is replaced when the canvas size changes (WebGL2 fixes texture
//     storage on first upload): read mask.texture each time, do not keep it.
//
//   planeMap(camera, renderer, rect, z, layer = hero) -> number[9] | null
//     Homography, a column-major mat3, taking terrain uv on the plane at
//     mesh-local z of layer's terrain to the unit square of rect (a client
//     rect): t = (0,0) at the rect's bottom-left, t.y = 1 at its top,
//     matching the texture's flipY. In GLSL: vec3 p = map * vec3(uv, 1.);
//     vec2 t = p.xy / p.z; p.z is positive inside the rect. null when the
//     rect cannot be mapped.
//
//   new SliceText(layer, { mask, slices = 48, baseZ, topZ, face = [1,1,1],
//                          sides = "rainbow", stripe = 2, bevel = 1 })
//     Extruded text on layer's terrain (either one; mesh-local z and the
//     placement below are that terrain's): an InstancedMesh of `slices`
//     PlaneGeometry(1,1) planes
//     stacked from topZ (instance 0, the face) down to baseZ (mesh-local z),
//     each covering the terrain-plane footprint of mask.box. Fragments map
//     through the homography to the mask and are discarded outside the glyphs
//     (mask alpha < 0.5). The face is `face`, shaded by the gradient of a
//     blurred mask (a soft bevel; bevel scales it, 0 = flat). The lower slices
//     are `sides`: an [r,g,b] color, or "rainbow" for thin stripes (`stripe`
//     slices each) cycling orange, yellow, white, cyan, violet, pink. Both
//     darken toward the base. Opaque and depth-tested, so terrain above it
//     hides it. Added to layer, hidden until placed.
//     .mesh                     the InstancedMesh
//     .map                      homography from the last place(), or null
//     .place(camera, renderer)  maps mask.box onto the plane at topZ, so the
//                               face covers the DOM box; true on success.
//                               Call while the DOM box is where the letters
//                               belong; between calls the letters stay put
//                               in the scene.
//     .setAmount(a)             0..1. At 1 the stack stands at baseZ..topZ;
//                               lower values sink it in z below the terrain;
//                               at 0 it is hidden. Not eased: ease in the
//                               caller.
//     .dispose()                removes and frees it.
//     Each mesh is in SWText.debug.slices until disposed.
//
//   setMound(owner, { mask, map, strength, level, lod, gain, floor,
//                     layer = hero })
//     Raises layer's terrain under a mask, so the terrain's contour lines
//     wrap around it. Height rises toward the cut-off of layer offset `level`
//     (uHeight plus that layer's depth offset, plus 0.01; the depth offset is
//     uDepthOffset * level on the hero terrain, and the footer shader's
//     level * |uDepthOffset * ((level + 0.3) * 2 - 1)| * 0.25 on the footer
//     terrain), never lowering it, where a heavily blurred mip (`lod`,
//     default the mip about 16 texels across) of mask.texture, looked up
//     through `map` (usually SliceText.map, of the same layer) and multiplied
//     by `gain` (default 2), is high; strength (0..1) scales it. The lookup
//     skips the terrain shader's uv wobble.
//     floor (a layer offset below level; default none) carves instead, for
//     terrain that is already high around the mask: with m the clamped
//     lookup, the height goes to the floor's cut-off where m reaches 0.15
//     (blended in from m = 0) and climbs from there to level's cut-off at
//     m = 1, lowering or raising it, so the contour lines step up toward the
//     mask from a trench around it.
//     Each terrain draws one mound: of its owners, the one with the highest
//     strength (above 0.002). Call every frame the values change. An owner
//     sits on one terrain at a time; setMound with another layer moves it.
//     The library writes the active mound's uniforms again before every
//     frame that terrain renders: the site's Theatre sheet adds every
//     uniform the material has when the sheet is built (these included) and
//     resets them whenever its animated values change.
//   clearMound(owner)
//
//   debug: { slices: [], mound: null, mounds: {} }
//     mound = the hero terrain's active setMound entry; mounds = the active
//     entry per terrain name (null when none).
//
// Any setup failure logs one console.warn and leaves that terrain (and the
// headlines on it) as the page had it.
(() => {
  "use strict";

  let warned = false;
  const warn = (...args) => {
    if (warned) return;
    warned = true;
    console.warn("SWText disabled:", ...args);
  };

  const HERO = "Layers Hero";
  const NAMES = [HERO, "Layers Footer"];
  const debug = { slices: [], mound: null, mounds: {} };

  // Per terrain name: the attached instance, its queued onTerrain callbacks,
  // its onFrame subscribers and frame clock, and its mound owners.
  const terrains = new Map(
    NAMES.map(name => [name, { name, layer: null, failed: false, pending: [], frames: [], lastStamp: -1, lastNow: 0, mounds: new Map(), empty: null }]),
  );
  for (const name of NAMES) debug.mounds[name] = null;
  let lastCamera = null;

  const terrainOf = name => {
    const t = terrains.get(name);
    if (!t) throw new Error(`SWText: no terrain named "${name}"`);
    return t;
  };

  const run = (cb, layer) => {
    try {
      cb(layer);
    } catch (err) {
      warn(err);
    }
  };

  function onTerrain(cb, { name = HERO } = {}) {
    const t = terrainOf(name);
    if (t.failed) return;
    if (t.layer) run(cb, t.layer);
    else t.pending.push(cb);
  }

  function onFrame(fn, { name = HERO } = {}) {
    const frames = terrainOf(name).frames;
    frames.push(fn);
    return () => {
      const i = frames.indexOf(fn);
      if (i >= 0) frames.splice(i, 1);
    };
  }

  function three(layer) {
    const mesh = layer.mesh;
    const material = mesh.material;
    return {
      get Vector3() {
        return (lastCamera ?? mesh).position.constructor;
      },
      Matrix4: mesh.matrixWorld.constructor,
      Texture: material.uniforms.tHeightNoise.value.constructor,
      InstancedMesh: mesh.constructor,
      PlaneGeometry: mesh.geometry.constructor,
      ShaderMaterial: Object.getPrototypeOf(material.constructor),
      InstancedBufferAttribute: mesh.instanceMatrix.constructor,
    };
  }

  // The given terrain layer, else the hero terrain; it must be attached.
  const needLayer = layer => {
    const found = layer ?? terrains.get(HERO).layer;
    if (!found || ![...terrains.values()].some(t => t.layer === found)) {
      throw new Error("SWText: terrain not attached yet; call from onTerrain");
    }
    return found;
  };
  // Any attached terrain (for constructors shared by both).
  const anyLayer = () => {
    for (const t of terrains.values()) if (t.layer) return t.layer;
    throw new Error("SWText: terrain not attached yet; call from onTerrain");
  };

  // Exact-count string replacement; a miss means the bundle changed.
  function replace(src, find, to, count) {
    const found = src.split(find).length - 1;
    if (found !== count) throw new Error(`shader anchor "${find}" found ${found} times, expected ${count}`);
    return src.split(find).join(to);
  }

  // Terrain height lookups go through swHeight, which adds the mound. The
  // mound is looked up without the shader's uv wobble (heightNoiseOffset,
  // up to 0.024 uv), which is large next to a letter.
  function patchShader(src) {
    src = replace(src, "texture2D(tHeightNoise,uv).r", "swHeight(uv)", 1);
    src = replace(src, "texture2D(tHeightNoise,uvc).r", "swHeight(uvc)", 2);
    src = replace(
      src,
      "vec2 heightNoiseOffset=noise.rg*uHeightNoiseStrength;",
      "vec2 heightNoiseOffset=noise.rg*uHeightNoiseStrength;swOffset=heightNoiseOffset;",
      1,
    );
    return replace(
      src,
      "float aastep(",
      "uniform sampler2D tSwMound;uniform mat3 uSwMoundMap;uniform float uSwMoundStrength;" +
        "uniform float uSwMoundLevel;uniform float uSwMoundLod;uniform float uSwMoundGain;" +
        "uniform float uSwMoundFloor;vec2 swOffset;" +
        "float swMound(vec2 uv){vec3 p=uSwMoundMap*vec3(uv,1.);if(p.z<=0.)return 0.;vec2 t=p.xy/p.z;" +
        "vec2 e=min(t,1.-t);if(min(e.x,e.y)<=0.)return 0.;" +
        "return clamp(textureLod(tSwMound,t,uSwMoundLod).a*uSwMoundGain,0.,1.)*smoothstep(0.,.06,min(e.x,e.y));}" +
        // The cut-off of layer offset l, as main() computes it for each
        // terrain (uIsFooter is declared above aastep).
        "float swCut(float l){return uHeight+(uIsFooter?l*abs(uDepthOffset*((l+.3)*2.-1.))*.25:uDepthOffset*l)+.01;}" +
        "float swHeight(vec2 uv){float h=texture2D(tHeightNoise,uv).r;if(uSwMoundStrength<=0.)return h;" +
        "float m=swMound(uv-swOffset);float top=swCut(uSwMoundLevel);" +
        "if(uSwMoundFloor<0.)return mix(h,max(h,top),uSwMoundStrength*m*m*(3.-2.*m));" +
        // Carved: the height falls to the floor's cut-off where the mound
        // starts and climbs to the plateau toward its core.
        "float c=mix(swCut(uSwMoundFloor),top,smoothstep(.15,1.,m));" +
        "return mix(h,c,uSwMoundStrength*smoothstep(0.,.15,m));}" +
        "float aastep(",
      1,
    );
  }

  function attach(layer) {
    const t = terrains.get(layer?.options?.name);
    if (!t || t.layer || t.failed) return;
    try {
      const mesh = layer.mesh;
      const material = mesh.material;
      const uniforms = material.uniforms;
      const fragmentShader = patchShader(material.fragmentShader);
      const Texture = uniforms.tHeightNoise.value.constructor;
      const empty = (t.empty = new Texture());
      Object.assign(uniforms, {
        tSwMound: { value: empty },
        uSwMoundMap: { value: [1, 0, 0, 0, 1, 0, 0, 0, 1] },
        uSwMoundStrength: { value: 0 },
        uSwMoundLevel: { value: 0.95 },
        uSwMoundLod: { value: 6 },
        uSwMoundGain: { value: 3 },
        uSwMoundFloor: { value: -1 },
      });
      material.fragmentShader = fragmentShader;
      material.needsUpdate = true;

      const frames = t.frames;
      const previous = mesh.onBeforeRender;
      mesh.onBeforeRender = function (renderer, scene, camera, ...rest) {
        previous?.call(this, renderer, scene, camera, ...rest);
        // Once per frame even if the scene renders more than once.
        const stamp = document.timeline?.currentTime ?? performance.now();
        if (stamp === t.lastStamp) return;
        t.lastStamp = stamp;
        const now = performance.now();
        const dt = t.lastNow ? Math.min(0.1, (now - t.lastNow) / 1000) : 0;
        t.lastNow = now;
        lastCamera = camera;
        for (const fn of [...frames]) {
          try {
            fn({ renderer, camera, dt, now, layer });
          } catch (err) {
            warn(err);
            const i = frames.indexOf(fn);
            if (i >= 0) frames.splice(i, 1);
          }
        }
        // Written again on every frame: the mask's texture is replaced when
        // its canvas resizes, and the site's Theatre sheet, which picked up
        // these uniforms with the material's own, resets them to their first
        // values whenever its animated values change.
        writeMound(t);
      };
      t.layer = layer;
    } catch (err) {
      t.failed = true;
      warn(err);
      return;
    }
    t.pending.splice(0).forEach(cb => run(cb, layer));
  }

  function glyphMask(element, { pad = 0.15, width = 2048, edge = 0.05 } = {}) {
    const { Texture } = three(anyLayer());
    const canvas = document.createElement("canvas");
    const range = document.createRange();
    let drawnFor = "";
    let fontsSeen = 0;
    document.fonts?.addEventListener("loadingdone", () => fontsSeen++);
    const mask = { canvas, texture: new Texture(canvas), box: null, update };

    function update() {
      const rect = element.getBoundingClientRect();
      if (rect.width < 1 || rect.height < 1) return false;
      const margin = pad * Math.max(rect.width, rect.height);
      const box = new DOMRect(rect.left - margin, rect.top - margin, rect.width + 2 * margin, rect.height + 2 * margin);
      mask.box = box;
      const glyphs = [];
      const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
      for (let node; (node = walker.nextNode()); ) {
        for (let i = 0; i < node.length; i++) {
          if (!node.data[i].trim()) continue;
          range.setStart(node, i);
          range.setEnd(node, i + 1);
          const r = range.getBoundingClientRect();
          if (r.width < 0.5 && r.height < 0.5) continue;
          glyphs.push([node.data[i], Math.round((r.left - box.left) * 2) / 2, Math.round((r.bottom - box.top) * 2) / 2]);
        }
      }
      const style = getComputedStyle(element);
      const key = `${Math.round(box.width)}x${Math.round(box.height)}|${style.fontStyle} ${style.fontWeight} ${style.fontSize} ${style.fontFamily}|${fontsSeen}|${glyphs.join("|")}`;
      if (key === drawnFor) return true;

      const scale = Math.min(width / box.width, 2048 / box.height);
      const cw = Math.max(1, Math.round(box.width * scale));
      const ch = Math.max(1, Math.round(box.height * scale));
      if (canvas.width !== cw || canvas.height !== ch) {
        canvas.width = cw;
        canvas.height = ch;
        mask.texture.dispose();
        mask.texture = new Texture(canvas);
      }
      const ctx = canvas.getContext("2d");
      const size = parseFloat(style.fontSize) * scale;
      ctx.clearRect(0, 0, cw, ch);
      ctx.font = `${style.fontStyle} ${style.fontWeight} ${size}px ${style.fontFamily}`;
      ctx.textBaseline = "alphabetic";
      ctx.fillStyle = "#000";
      // Range rects span the font's ascent and descent: the baseline is the
      // bottom minus the descent.
      const descent = ctx.measureText("Hg").fontBoundingBoxDescent ?? size * 0.22;
      // The blur is a shadow cast back onto the canvas from glyphs drawn off
      // its left edge, which works where ctx.filter does not.
      const shift = edge > 0 ? cw * 2 : 0;
      ctx.shadowColor = "#000";
      ctx.shadowBlur = edge > 0 ? size * edge : 0;
      ctx.shadowOffsetX = shift;
      for (const [c, x, y] of glyphs) ctx.fillText(c, x * scale - shift, y * scale - descent);
      ctx.shadowColor = "transparent";
      mask.texture.needsUpdate = true;
      drawnFor = key;
      return true;
    }
    return mask;
  }

  // Homography taking quad p0..p3 (corners for unit-square points (0,0),
  // (1,0), (1,1), (0,1)) back to the unit square, as a column-major mat3.
  // Square-to-quad after Heckbert, then inverted.
  function squareFrom([x0, y0], [x1, y1], [x2, y2], [x3, y3]) {
    const dx1 = x1 - x2, dx2 = x3 - x2, dx3 = x0 - x1 + x2 - x3;
    const dy1 = y1 - y2, dy2 = y3 - y2, dy3 = y0 - y1 + y2 - y3;
    const den = dx1 * dy2 - dx2 * dy1;
    if (!den) return null;
    const g = (dx3 * dy2 - dx2 * dy3) / den;
    const h = (dx1 * dy3 - dx3 * dy1) / den;
    // Row-major square-to-quad.
    const a = x1 - x0 + g * x1, b = x3 - x0 + h * x3, c = x0;
    const d = y1 - y0 + g * y1, e = y3 - y0 + h * y3, f = y0;
    // Adjugate; the determinant's scale cancels in the divide by p.z.
    const m = [
      e - f * h, c * h - b, b * f - c * e,
      f * g - d, a - c * g, c * d - a * f,
      d * h - e * g, b * g - a * h, a * e - b * d,
    ];
    if (!m.every(Number.isFinite)) return null;
    // Keep p.z positive inside the quad, which the shaders rely on.
    const sign = Math.sign(m[6] * x0 + m[7] * y0 + m[8]) || 1;
    return [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]].map(v => v * sign);
  }

  // Projects client point (x, y) onto the plane at mesh-local z and returns
  // its mesh-local [x, y], or null when the view ray misses the plane.
  function toLocal(x, y, z, camera, canvasRect, inverse) {
    const V3 = camera.position.constructor;
    const nx = ((x - canvasRect.left) / canvasRect.width) * 2 - 1;
    const ny = -((y - canvasRect.top) / canvasRect.height) * 2 + 1;
    const a = new V3(nx, ny, -1).unproject(camera).applyMatrix4(inverse);
    const b = new V3(nx, ny, 1).unproject(camera).applyMatrix4(inverse);
    if (b.z === a.z) return null;
    const k = (z - a.z) / (b.z - a.z);
    if (!(k > 0)) return null;
    return [a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k];
  }

  // Corners of rect on the plane at z, mesh-local, in the order
  // bottom-left, bottom-right, top-right, top-left.
  function corners(camera, renderer, rect, z, layer) {
    const mesh = needLayer(layer).mesh;
    const inverse = new mesh.matrixWorld.constructor().copy(mesh.matrixWorld).invert();
    const canvasRect = renderer.domElement.getBoundingClientRect();
    const out = [
      [rect.left, rect.bottom],
      [rect.right, rect.bottom],
      [rect.right, rect.top],
      [rect.left, rect.top],
    ].map(([x, y]) => toLocal(x, y, z, camera, canvasRect, inverse));
    return out.every(Boolean) ? out : null;
  }

  function planeMap(camera, renderer, rect, z, layer) {
    const quad = corners(camera, renderer, rect, z, layer);
    if (!quad) return null;
    return squareFrom(...quad.map(([x, y]) => [x / 5 + 0.5, y / 5 + 0.5]));
  }

  const SLICE_VERTEX = `varying vec3 vLocal;varying float vSlice;
void main(){vec4 p=vec4(position,1.);
#ifdef USE_INSTANCING
p=instanceMatrix*p;
#endif
vLocal=p.xyz;vSlice=float(gl_InstanceID);gl_Position=projectionMatrix*modelViewMatrix*p;}`;

  const SLICE_FRAGMENT = `uniform sampler2D tMask;uniform mat3 uMap;uniform vec2 uTexel;uniform float uCount;
uniform vec3 uFace;uniform vec3 uSide;uniform float uRainbow;uniform float uStripe;uniform float uBevel;
varying vec3 vLocal;varying float vSlice;
float maskAt(vec2 t,float lod){return textureLod(tMask,t,lod).a;}
vec3 band(float i){float k=mod(floor(i/uStripe),6.);
if(k<.5)return vec3(1.,.42,.08);if(k<1.5)return vec3(1.,.82,.18);if(k<2.5)return vec3(.95,.95,.93);
if(k<3.5)return vec3(.18,.82,1.);if(k<4.5)return vec3(.52,.32,1.);return vec3(1.,.32,.66);}
void main(){vec3 p=uMap*vec3(vLocal.xy/5.+.5,1.);if(p.z<=0.)discard;vec2 t=p.xy/p.z;
if(t.x<0.||t.x>1.||t.y<0.||t.y>1.)discard;
if(texture2D(tMask,t).a<.5)discard;
vec3 c;
if(vSlice<.5){
float l=3.;vec2 d=uTexel*8.;
float gx=maskAt(t+vec2(d.x,0.),l)-maskAt(t-vec2(d.x,0.),l);
float gy=maskAt(t+vec2(0.,d.y),l)-maskAt(t-vec2(0.,d.y),l);
vec3 n=normalize(vec3(-gx*uBevel,-gy*uBevel,.5));
float lit=dot(n,normalize(vec3(-.45,.55,.7)));
c=uFace*mix(1.,.62+.42*lit,clamp(uBevel,0.,1.));
}else{
float f=(vSlice-1.)/max(uCount-2.,1.);
c=(uRainbow>.5?band(vSlice-1.):uSide)*mix(.95,.3,f);
}
gl_FragColor=vec4(c,1.);
#include <colorspace_fragment>
}`;

  class SliceText {
    constructor(layer, { mask, slices = 48, baseZ, topZ, face = [1, 1, 1], sides = "rainbow", stripe = 2, bevel = 1 } = {}) {
      if (!mask) throw new Error("SliceText needs a mask");
      if (!(topZ > baseZ)) throw new Error("SliceText needs topZ > baseZ");
      const T = three(layer);
      this.layer = layer;
      this.mask = mask;
      this.count = Math.max(2, Math.round(slices));
      this.baseZ = baseZ;
      this.topZ = topZ;
      this.amount = 0;
      this.map = null;
      this.footprint = null;
      this._m = new T.Matrix4();
      const rainbow = sides === "rainbow";
      this.uniforms = {
        tMask: { value: mask.texture },
        uMap: { value: [1, 0, 0, 0, 1, 0, 0, 0, 1] },
        uTexel: { value: [1 / 2048, 1 / 1024] },
        uCount: { value: this.count },
        uFace: { value: face },
        uSide: { value: rainbow ? [1, 1, 1] : sides },
        uRainbow: { value: rainbow ? 1 : 0 },
        uStripe: { value: Math.max(1, stripe) },
        uBevel: { value: bevel },
      };
      const material = new T.ShaderMaterial({
        vertexShader: SLICE_VERTEX,
        fragmentShader: SLICE_FRAGMENT,
        uniforms: this.uniforms,
        transparent: false,
        depthTest: true,
        depthWrite: true,
      });
      const mesh = (this.mesh = new T.InstancedMesh(new T.PlaneGeometry(1, 1), material, this.count));
      mesh.name = "SWText slices";
      mesh.frustumCulled = false;
      mesh.visible = false;
      mesh.position.copy(layer.mesh.position);
      mesh.quaternion.copy(layer.mesh.quaternion);
      mesh.scale.copy(layer.mesh.scale);
      mesh.onBeforeRender = () => {
        this.uniforms.tMask.value = this.mask.texture;
      };
      layer.add(mesh);
      debug.slices.push(mesh);
    }

    place(camera, renderer) {
      const box = this.mask.box;
      if (!box) return false;
      const quad = corners(camera, renderer, box, this.topZ, this.layer);
      if (!quad) return false;
      const map = squareFrom(...quad.map(([x, y]) => [x / 5 + 0.5, y / 5 + 0.5]));
      if (!map) return false;
      const xs = quad.map(q => q[0]);
      const ys = quad.map(q => q[1]);
      const x0 = Math.min(...xs), x1 = Math.max(...xs), y0 = Math.min(...ys), y1 = Math.max(...ys);
      this.footprint = [(x0 + x1) / 2, (y0 + y1) / 2, x1 - x0, y1 - y0];
      this.map = map;
      this.uniforms.uMap.value = map;
      this.uniforms.tMask.value = this.mask.texture;
      this.uniforms.uTexel.value = [1 / this.mask.canvas.width, 1 / this.mask.canvas.height];
      this._write();
      return true;
    }

    setAmount(a) {
      this.amount = Math.min(1, Math.max(0, a || 0));
      this._write();
    }

    _write() {
      const mesh = this.mesh;
      mesh.visible = !!this.footprint && this.amount > 0.002;
      if (!this.footprint) return;
      const [cx, cy, sx, sy] = this.footprint;
      // Sinks until the face is below the lowest terrain layer (z 0).
      const drop = (1 - this.amount) * (this.topZ + 0.05);
      const step = (this.topZ - this.baseZ) / (this.count - 1);
      for (let i = 0; i < this.count; i++) {
        this._m.makeScale(sx, sy, 1).setPosition(cx, cy, this.topZ - i * step - drop);
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
      const i = debug.slices.indexOf(mesh);
      if (i >= 0) debug.slices.splice(i, 1);
    }
  }

  // Draws the strongest of a terrain's mound owners on that terrain.
  function applyMound(t) {
    let best = null;
    for (const entry of t.mounds.values()) {
      if (entry.strength > 0.002 && entry.map && (!best || entry.strength > best.strength)) best = entry;
    }
    debug.mounds[t.name] = best;
    if (t.name === HERO) debug.mound = best;
    writeMound(t);
  }

  // Sets t's mound uniforms from its active entry.
  function writeMound(t) {
    if (!t.layer) return;
    const best = debug.mounds[t.name];
    const u = t.layer.mesh.material.uniforms;
    if (!best) {
      u.uSwMoundStrength.value = 0;
      u.tSwMound.value = t.empty;
      return;
    }
    const c = best.mask.canvas;
    u.tSwMound.value = best.mask.texture;
    u.uSwMoundMap.value = best.map;
    u.uSwMoundStrength.value = best.strength;
    u.uSwMoundLevel.value = best.level;
    u.uSwMoundLod.value = best.lod ?? Math.max(0, Math.log2(Math.max(c.width, c.height)) - 4);
    u.uSwMoundGain.value = best.gain ?? 2;
    u.uSwMoundFloor.value = best.floor ?? -1;
  }

  function setMound(owner, { mask, map, strength = 0, level = 0.95, lod, gain, floor, layer } = {}) {
    const target = needLayer(layer);
    for (const t of terrains.values()) {
      if (t.layer === target) {
        t.mounds.set(owner, { owner, mask, map, strength: Math.min(1, Math.max(0, strength)), level, lod, gain, floor });
        applyMound(t);
      } else if (t.mounds.delete(owner)) {
        applyMound(t);
      }
    }
  }

  function clearMound(owner) {
    for (const t of terrains.values()) if (t.mounds.delete(owner)) applyMound(t);
  }

  window.SWText = { onTerrain, three, onFrame, glyphMask, planeMap, SliceText, setMound, clearMound, debug };

  const queue = (window.__swLayers = window.__swLayers || []);
  queue.forEach(attach);
  queue.push = function (...items) {
    items.forEach(attach);
    return Array.prototype.push.apply(this, items);
  };
})();
