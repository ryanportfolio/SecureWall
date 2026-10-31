// Raises the hero headline out of the secwall.org terrain.
//
// The terrain is 20 stacked planes; each draws where a height texture minus its
// layer offset clears a threshold, and takes its color from its depth. This
// script adds a height map of the headline to that height, so the letters
// become terraces with the same rainbow sides, shadows and highlights.
//
// patch-secwall-site.mjs serves this file as /build/sw-terrain-text-<hash>.js
// and patches the bundle to push each terrain instance onto window.__swLayers.
// If anything here fails, the page keeps its normal DOM headline.
(() => {
  "use strict";

  if (matchMedia("(prefers-reduced-motion: reduce)").matches) return;

  // Layer offsets run 0..0.95 (the 20th wraps to 0), and each layer sits at
  // that mesh-local z. At rest the letters sit on the highest layer, so the
  // headline box is projected onto that plane.
  const TOP_LAYER = 0.95;
  // Level the letters sink to when the hero is gone, below most terrain.
  const SUNK_LAYER = 0.2;
  // Layers sit 0.05 apart and, seen from this camera, about 80 px apart on
  // screen, so letters spread over several layers print offset copies. The
  // letters therefore sit on exactly one layer: the level is snapped to the
  // nearest layer offset and the shader selects the layer within half a step.
  const LAYER_STEP = 0.05;
  // Blur radius as a fraction of the font size, for the soft letter edge.
  const EDGE = 0.05;
  // Letter color (linear) and the drop shadow on the terrain below: strength
  // and offset in ems, down and to the right like the scene's light.
  const COLOR = [0.95, 0.95, 0.96];
  const SHADOW = 0.45;
  const SHADOW_SHIFT = [0.05, 0.1];
  // A tighter contact shadow, and the strength of the rainbow rim.
  const CONTACT = 0.35;
  const CONTACT_SHIFT = [0.015, 0.03];
  const RIM = 0.65;
  const CANVAS_WIDTH = 2048;

  const queue = (window.__swLayers = window.__swLayers || []);
  const attach = layer => {
    // The page builds "Layers Hero" and "Layers Footer"; only the hero gets text.
    if (layer?.options?.name !== "Layers Hero" || layer.__swText) return;
    layer.__swText = true;
    try {
      setup(layer);
    } catch (err) {
      console.warn("terrain text disabled:", err);
      document.documentElement.classList.remove("sw-terrain-text");
    }
  };
  queue.forEach(attach);
  queue.push = (...items) => {
    items.forEach(attach);
    return Array.prototype.push.apply(queue, items);
  };

  // Exact-count string replacement; a miss means the bundle changed.
  function replace(src, find, to, count) {
    const found = src.split(find).length - 1;
    if (found !== count) throw new Error(`shader anchor "${find}" found ${found} times, expected ${count}`);
    return src.split(find).join(to);
  }

  // The letters are a plate on one layer (uSwTextLevel, a layer offset)
  // rather than extra height on top of the terrain: added height would put
  // each letter's top on whatever layer the ground under it reaches, and the
  // layers' parallax would tear the word apart. The face is tinted
  // uSwTextColor. Around it, in screen space (uSwScreenBox, the headline box
  // in drawing-buffer pixels), blurrier mips of the same texture give a soft
  // drop shadow, a contact shadow and a rainbow rim like the terrain's ridge
  // edges. The scene's post-processing lifts bright terrain toward white, and
  // there only the rim keeps the edge readable. The text is looked up without
  // the shader's uv wobble (heightNoiseOffset), which is large next to the
  // size of a letter.
  function patchShader(src) {
    src = replace(src, "texture2D(tHeightNoise,uv).r", "swHeight(uv)", 1);
    src = replace(src, "texture2D(tHeightNoise,uvc).r", "swHeight(uvc)", 2);
    src = replace(
      src,
      "vec2 heightNoiseOffset=noise.rg*uHeightNoiseStrength;",
      "vec2 heightNoiseOffset=noise.rg*uHeightNoiseStrength;swOffset=heightNoiseOffset;",
      1,
    );
    src = replace(
      src,
      "gl_FragColor=vec4(c,n);",
      // m is the letter face. Everything outside it gets the shadows and the
      // rim, whichever layer draws it: terrain that already reaches the top
      // layer would otherwise merge with the face.
      "if(uSwTextAmount>0.){float m=swOnText()?smoothstep(.35,.9,swMask(vUv,0.))*uSwTextAmount:0.;" +
        "vec2 s=(gl_FragCoord.xy-uSwScreenBox.xy)/uSwScreenBox.zw;" +
        "float sh=max(uSwStrength.x*swBox(s-uSwShift.xy,3.),uSwStrength.y*swBox(s-uSwShift.zw,1.));" +
        "c*=1.-sh*(1.-m);" +
        "float rim=smoothstep(.04,.3,swBox(s,1.5))*(1.-m);" +
        "vec3 bands=.55+.45*cos(6.2832*(s.x*.8+s.y*.35+vec3(0.,.33,.67)));" +
        "c=mix(c,bands,rim*uSwStrength.z);" +
        "c=mix(c,uSwTextColor,m);}" +
        "gl_FragColor=vec4(c,n);",
      1,
    );
    return replace(
      src,
      "float aastep(",
      "uniform sampler2D tSwText;uniform mat3 uSwTextMap;" +
        "uniform float uSwTextAmount;uniform float uSwTextLevel;uniform float uSwTextDepth;" +
        "uniform vec3 uSwTextColor;uniform vec4 uSwScreenBox;uniform vec4 uSwShift;uniform vec3 uSwStrength;vec2 swOffset;" +
        "bool swOnText(){return abs(vLayerOffset-uSwTextLevel)<uSwTextDepth;}" +
        "float swBox(vec2 s,float bias){if(s.x<0.||s.x>1.||s.y<0.||s.y>1.)return 0.;return texture2D(tSwText,s,bias).a;}" +
        "float swMask(vec2 uv,float bias){vec3 p=uSwTextMap*vec3(uv,1.);vec2 t=p.xy/p.z;" +
        "if(p.z<=0.||t.x<0.||t.x>1.||t.y<0.||t.y>1.)return 0.;return texture2D(tSwText,t,bias).a;}" +
        "float swHeight(vec2 uv){float h=texture2D(tHeightNoise,uv).r;" +
        "if(uSwTextAmount<=0.||!swOnText())return h;" +
        // The plate stands well above the layer's cut-off and the soft mask is
        // sharpened around 0.5, so a letter's outline lands on its true edge
        // whatever the ground height under it.
        "float top=uHeight+uDepthOffset*uSwTextLevel+.1;" +
        "return mix(h,max(h,top),uSwTextAmount*smoothstep(.3,.7,swMask(uv-swOffset,0.)));}" +
        "float aastep(",
      1,
    );
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
    // Keep p.z positive inside the box, which the shader relies on.
    const sign = Math.sign(m[6] * x0 + m[7] * y0 + m[8]) || 1;
    return [m[0], m[3], m[6], m[1], m[4], m[7], m[2], m[5], m[8]].map(v => v * sign);
  }

  function setup(layer) {
    const hero = document.querySelector(".js-hero");
    const title = hero?.querySelector("h1");
    if (!title) throw new Error("hero headline not found");
    const mesh = layer.mesh;
    const material = mesh.material;
    const uniforms = material.uniforms;
    const Texture = uniforms.tHeightNoise.value.constructor;

    const canvas = document.createElement("canvas");
    let texture = new Texture(canvas);
    Object.assign(uniforms, {
      tSwText: { value: texture },
      uSwTextMap: { value: [1, 0, 0, 0, 1, 0, 0, 0, 1] },
      uSwTextAmount: { value: 0 },
      uSwTextLevel: { value: SUNK_LAYER },
      uSwTextDepth: { value: LAYER_STEP / 2 },
      uSwTextColor: { value: COLOR },
      uSwScreenBox: { value: [0, 0, 1, 1] },
      uSwShift: { value: [0, 0, 0, 0] },
      uSwStrength: { value: [0, 0, 0] },
    });
    material.fragmentShader = patchShader(material.fragmentShader);
    material.needsUpdate = true;

    let drawnFor = "";
    let ready = false;
    // Draws each character of the headline where the page lays it out, with a
    // soft edge, so the terrain letters match the DOM letters. Redraws whenever
    // a character moves: the page's intro animation translates them, so a
    // measurement taken mid-animation would leave the letters out of place.
    const range = document.createRange();
    const draw = () => {
      const box = title.getBoundingClientRect();
      if (box.width < 1 || box.height < 1) return false;
      const glyphs = [];
      const walker = document.createTreeWalker(title, NodeFilter.SHOW_TEXT);
      for (let node; (node = walker.nextNode()); ) {
        for (let i = 0; i < node.length; i++) {
          if (!node.data[i].trim()) continue;
          range.setStart(node, i);
          range.setEnd(node, i + 1);
          const r = range.getBoundingClientRect();
          glyphs.push([node.data[i], Math.round(r.left - box.left), Math.round(r.bottom - box.top)]);
        }
      }
      const style = getComputedStyle(title);
      const key = `${Math.round(box.width)}x${Math.round(box.height)}:${style.fontSize}:${glyphs.join("|")}`;
      if (key === drawnFor) return true;
      const scale = CANVAS_WIDTH / box.width;
      const height = Math.min(2048, Math.round(box.height * scale));
      // WebGL2 gives a texture fixed storage on its first upload, so a canvas
      // of a new size needs a new texture.
      if (canvas.width !== CANVAS_WIDTH || canvas.height !== height) {
        canvas.width = CANVAS_WIDTH;
        canvas.height = height;
        texture.dispose();
        texture = new Texture(canvas);
        uniforms.tSwText.value = texture;
      }
      const sy = canvas.height / box.height;
      const ctx = canvas.getContext("2d");
      const size = parseFloat(style.fontSize) * scale;
      ctx.clearRect(0, 0, canvas.width, canvas.height);
      ctx.font = `${style.fontWeight} ${size}px ${style.fontFamily}`;
      ctx.textBaseline = "alphabetic";
      const descent = ctx.measureText("Hg").fontBoundingBoxDescent ?? size * 0.22;
      // The blur comes from a shadow cast back onto the canvas from glyphs
      // drawn off its right edge, which works where ctx.filter does not.
      const shift = canvas.width * 2;
      ctx.shadowColor = "#000";
      ctx.shadowBlur = size * EDGE;
      ctx.shadowOffsetX = shift;
      ctx.fillStyle = "#000";
      for (const [ch, x, y] of glyphs) ctx.fillText(ch, x * scale - shift, y * sy - descent);
      texture.needsUpdate = true;
      drawnFor = key;
      return true;
    };

    // Projects a client point onto the terrain plane and returns its uv.
    let V3, inverse;
    const toUv = (x, y, camera, rect) => {
      const nx = ((x - rect.left) / rect.width) * 2 - 1;
      const ny = -((y - rect.top) / rect.height) * 2 + 1;
      const a = new V3(nx, ny, -1).unproject(camera).applyMatrix4(inverse);
      const b = new V3(nx, ny, 1).unproject(camera).applyMatrix4(inverse);
      const k = (TOP_LAYER - a.z) / (b.z - a.z);
      return [(a.x + (b.x - a.x) * k) / 5 + 0.5, (a.y + (b.y - a.y) * k) / 5 + 0.5];
    };

    // Projective map from terrain uv to texture coordinates, built from the
    // four corners of the headline box on the top-layer plane. The camera looks
    // across the terrain at a steep angle, so three corners (an affine map)
    // would put the fourth far from the box. t = (0,0) at bottom-left and
    // t.y = 1 at the top, matching the texture's flipY.
    const place = (camera, renderer) => {
      V3 ||= camera.position.constructor;
      inverse ||= mesh.matrixWorld.clone();
      inverse.copy(mesh.matrixWorld).invert();
      const rect = renderer.domElement.getBoundingClientRect();
      const box = title.getBoundingClientRect();
      const map = squareFrom(
        toUv(box.left, box.bottom, camera, rect),
        toUv(box.right, box.bottom, camera, rect),
        toUv(box.right, box.top, camera, rect),
        toUv(box.left, box.top, camera, rect),
      );
      if (map) uniforms.uSwTextMap.value = map;
      const dpr = renderer.domElement.width / rect.width;
      const em = parseFloat(getComputedStyle(title).fontSize);
      // Drawing-buffer y runs up, so "down" is a negative shift.
      uniforms.uSwScreenBox.value = [
        (box.left - rect.left) * dpr,
        (rect.bottom - box.bottom) * dpr,
        box.width * dpr,
        box.height * dpr,
      ];
      uniforms.uSwShift.value = [
        (SHADOW_SHIFT[0] * em) / box.width,
        -(SHADOW_SHIFT[1] * em) / box.height,
        (CONTACT_SHIFT[0] * em) / box.width,
        -(CONTACT_SHIFT[1] * em) / box.height,
      ];
    };

    // The headline follows the hero: it rises while the hero shows and sinks
    // as the hero fades on scroll. Its place is fixed while it is moving, so it
    // travels with the terrain instead of sliding across it.
    let last = performance.now();
    let rise = 0;
    const previous = mesh.onBeforeRender;
    mesh.onBeforeRender = function (renderer, scene, camera, ...rest) {
      previous?.call(this, renderer, scene, camera, ...rest);
      const now = performance.now();
      const dt = Math.min(0.1, (now - last) / 1000);
      last = now;
      const css = getComputedStyle(hero);
      const shown = css.visibility === "hidden" ? 0 : parseFloat(css.opacity);
      if (shown > 0.99 && draw()) place(camera, renderer);
      const target = drawnFor ? shown : 0;
      // Ease toward the target; the letters rise from SUNK_LAYER to the top
      // layer, and their mask fades only near the bottom.
      rise += (target - rise) * Math.min(1, dt * 2.5);
      const eased = rise * rise * (3 - 2 * rise);
      const level = SUNK_LAYER + (TOP_LAYER - SUNK_LAYER) * eased;
      uniforms.uSwTextLevel.value = Math.round(level / LAYER_STEP) * LAYER_STEP;
      uniforms.uSwTextAmount.value = Math.min(1, rise * 4);
      // Shadows and rim are pinned to the headline's screen box, so they fade
      // out as soon as the letters start to move.
      const pinned = eased ** 4;
      uniforms.uSwStrength.value = [SHADOW * pinned, CONTACT * pinned, RIM * pinned];
      if (!ready && drawnFor) {
        ready = true;
        document.documentElement.classList.add("sw-terrain-text");
      }
    };

    const redraw = () => (drawnFor = "");
    new ResizeObserver(redraw).observe(title);
    document.fonts?.ready.then(redraw);
  }
})();
