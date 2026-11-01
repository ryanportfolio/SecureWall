// Block section: "Block by default" as a charcoal panel with the words cut
// out of it (concept 06). The panel slides up over the terrain while the
// title shows; the live terrain glows through the letter-shaped holes.
//
// No 3D: the panel is one page canvas over the WebGL canvas, drawn only when
// the viewport, the font or the text changes, and moved with a CSS transform.
// Each hole gets a cut wall along its top and left edges and a soft shadow
// the panel casts onto the terrain inside it; the panel's edges cast a soft
// shadow onto the terrain beyond them.
//
// The panel follows the h2's visible state (lowest computed opacity among the
// h2, its ancestors and its words). The site slides the words in from the
// left as the title shows and out to the right as it hides further down the
// page, so the words' x offset gives the direction: before the section the
// panel waits below the viewport and its top edge slides up as the title
// shows; past it, the panel leaves upward (its bottom edge slides up) and
// comes back down from the top when scrolling back.
//
// <html> gets sw-block-3d as soon as the section is set up, and block.css
// makes the DOM h2 transparent from then on; the h2 keeps its text in the
// accessibility tree. The canvas is decorative (aria-hidden).
//
// Frames: the site stops WebGL rendering (and so SWText.onFrame) past
// .js-webgl-scroll-end. While the panel shows, a requestAnimationFrame
// watchdog hides it once frames stop arriving; the next frame shows it again.
(() => {
  "use strict";
  const SW = window.SWText;
  if (!SW) return;

  // Panel colours. The canvas sits above the WebGL canvas, so the site's
  // post-processing does not lift them: PANEL is the on-screen colour.
  const PANEL = "#1a1a1a";
  const WALL = "#2b2b2b";
  const EDGE = "rgba(255, 255, 255, 0.09)";
  const HOLE_SHADOW = "rgba(0, 0, 0, 0.62)";
  const EDGE_SHADOW = 0.58; // alpha at the panel edge, fading out over BAND
  const GRAIN = 7; // grain alpha, out of 255

  // Layout, in fractions of the viewport. The panel's top edge rests TOP_REST
  // below the viewport top, leaving a strip of terrain under the site header.
  // The letters fill the panel inside SIDE (each side), PAD_TOP and
  // PAD_BOTTOM; LINE_GAP is the gap between line boxes, in font sizes.
  const TOP_REST = 0.15;
  const TOP_MIN = 104; // px, clears the site header's links
  const SIDE = 0.055;
  const PAD_TOP = 0.065;
  const PAD_BOTTOM = 0.06;
  const LINE_GAP = 0.11;
  // Cut depth and hole shadow, in font sizes.
  const WALL_X = 0.012;
  const WALL_Y = 0.03;
  const SHADOW_BLUR = 0.06;
  const SHADOW_X = 0.02;
  const SHADOW_Y = 0.05;
  // Soft shadow band beyond the panel's top and bottom edges, px.
  const BAND = 90;
  const MAX_DPR = 2;
  // Watchdog: rAF ticks without a WebGL frame before the panel hides.
  const STALE_TICKS = 4;

  SW.onTerrain(() => {
    const root = document.documentElement;
    // From here on the DOM h2 never shows its own text (block.css).
    root.classList.add("sw-block-3d");

    let title = null;
    let chain = null; // elements whose opacity and visibility count
    let words = null; // the h2's word elements
    let panel = null;
    let grain = null;
    let key = "";
    let fontWait = "";
    let travel = [0, 0]; // [down, up] px offsets that put the panel off screen
    let dir = -1; // -1: before the section (panel below), 1: past it (above)
    let shownNow = false;
    let lastY = NaN;
    let ticks = 0;
    let watching = false;
    const scratch = [document.createElement("canvas"), document.createElement("canvas")];

    function build() {
      words = [...title.children].filter(d => d.textContent.trim());
      if (words.length !== 3) throw new Error("block title words not found");
      chain = [title, ...title.querySelectorAll("*")];
      for (let e = title.parentElement; e && e !== root; e = e.parentElement) chain.push(e);
      panel = document.createElement("canvas");
      panel.className = "sw-block-panel";
      panel.setAttribute("aria-hidden", "true");
      panel.dataset.swSection = "block";
      document.body.append(panel);
    }

    // Lowest opacity along the chain (0 if any is hidden); sets dir from the
    // words' horizontal offset while they move.
    function shownAmount() {
      let min = 1;
      let x = 0;
      for (const e of chain) {
        const css = getComputedStyle(e);
        if (css.visibility === "hidden" || css.display === "none") return 0;
        const o = parseFloat(css.opacity);
        if (o < min) min = o;
        if (e.parentElement === title && css.transform.startsWith("matrix(")) x += parseFloat(css.transform.split(",")[4]);
      }
      if (x > 0.5) dir = 1;
      else if (x < -0.5) dir = -1;
      return min > 0 ? min : 0;
    }

    function grainPattern(ctx) {
      const n = 160;
      const c = document.createElement("canvas");
      c.width = c.height = n;
      const g = c.getContext("2d");
      const img = g.createImageData(n, n);
      for (let i = 0; i < img.data.length; i += 4) {
        const light = Math.random() < 0.5;
        img.data[i] = img.data[i + 1] = img.data[i + 2] = light ? 255 : 0;
        img.data[i + 3] = Math.round(Math.random() * GRAIN);
      }
      g.putImageData(img, 0, 0);
      return ctx.createPattern(c, "repeat");
    }

    // The panel with the words cut out, in `color`, on scratch canvas i.
    function stencil(i, w, h, color, lines, k) {
      const c = scratch[i];
      if (c.width !== w || c.height !== h) {
        c.width = w;
        c.height = h;
      }
      const ctx = c.getContext("2d");
      ctx.globalCompositeOperation = "source-over";
      ctx.fillStyle = color;
      ctx.fillRect(0, 0, w, h);
      if (i === 0) {
        grain ||= grainPattern(ctx);
        ctx.fillStyle = grain;
        ctx.fillRect(0, 0, w, h);
      }
      ctx.globalCompositeOperation = "destination-out";
      ctx.font = lines.font;
      ctx.fillStyle = "#000";
      for (const l of lines) ctx.fillText(l.text, l.x * k, l.y * k);
      ctx.globalCompositeOperation = "source-over";
      return c;
    }

    // Redraws the panel when the viewport, the text or the font changed.
    function draw() {
      const W = innerWidth;
      const H = innerHeight;
      const css = getComputedStyle(title);
      const family = css.fontFamily;
      const weight = css.fontWeight;
      const texts = words.map(w => w.textContent.trim());
      const loaded = document.fonts?.check(`${weight} 100px ${family}`) ?? true;
      const k = Math.min(MAX_DPR, devicePixelRatio || 1);
      const next = `${W}x${H}@${k}|${weight} ${family}|${texts.join(" ")}|${loaded}`;
      if (next === key) return;
      key = next;
      if (!loaded && fontWait !== family) {
        fontWait = family;
        document.fonts.load(`${weight} 100px ${family}`).then(() => (key = ""), () => {});
      }

      const top = Math.round(Math.max(TOP_MIN, H * TOP_REST));
      const PH = H - top; // panel height, CSS px
      const CH = PH + 2 * BAND;
      travel = [H - top + BAND, H + BAND];

      // Measure at 100 px, then scale the block of lines to fit the panel.
      const ctx0 = scratch[1].getContext("2d");
      ctx0.font = `${weight} 100px ${family}`;
      const m = texts.map(t => ctx0.measureText(t));
      const boxW = Math.max(...m.map(x => x.actualBoundingBoxLeft + x.actualBoundingBoxRight));
      const boxH = m.reduce((s, x) => s + x.actualBoundingBoxAscent + x.actualBoundingBoxDescent, 0) + LINE_GAP * 100 * (m.length - 1);
      const areaW = W * (1 - 2 * SIDE);
      const areaH = PH - H * (PAD_TOP + PAD_BOTTOM);
      const s = Math.min(areaW / boxW, areaH / boxH);
      const size = 100 * s;
      const lines = [];
      lines.font = `${weight} ${size * k}px ${family}`;
      let y = H * PAD_TOP + (areaH - boxH * s) / 2;
      m.forEach((x, i) => {
        const l = x.actualBoundingBoxLeft * s;
        const r = x.actualBoundingBoxRight * s;
        y += x.actualBoundingBoxAscent * s;
        lines.push({ text: texts[i], x: W / 2 - (r - l) / 2, y });
        y += x.actualBoundingBoxDescent * s + LINE_GAP * size;
      });

      const w = Math.round(W * k);
      const ph = Math.round(PH * k);
      const ch = Math.round(CH * k);
      const band = Math.round(BAND * k);
      if (panel.width !== w || panel.height !== ch) {
        panel.width = w;
        panel.height = ch;
      }
      panel.style.top = `${top - BAND}px`;
      panel.style.height = `${CH}px`;
      const front = stencil(0, w, ph, PANEL, lines, k);
      const wall = stencil(1, w, ph, WALL, lines, k);
      const ctx = panel.getContext("2d");
      ctx.clearRect(0, 0, w, ch);
      // Soft shadows on the terrain beyond the top and bottom edges.
      const up = ctx.createLinearGradient(0, 0, 0, band);
      up.addColorStop(0, "rgba(0,0,0,0)");
      up.addColorStop(0.55, `rgba(0,0,0,${EDGE_SHADOW * 0.35})`);
      up.addColorStop(1, `rgba(0,0,0,${EDGE_SHADOW})`);
      ctx.fillStyle = up;
      ctx.fillRect(0, 0, w, band);
      const down = ctx.createLinearGradient(0, band + ph, 0, ch);
      down.addColorStop(0, `rgba(0,0,0,${EDGE_SHADOW})`);
      down.addColorStop(0.45, `rgba(0,0,0,${EDGE_SHADOW * 0.35})`);
      down.addColorStop(1, "rgba(0,0,0,0)");
      ctx.fillStyle = down;
      ctx.fillRect(0, band + ph, w, ch - band - ph);
      // Inside the holes: the panel's shadow on the terrain, then the cut
      // wall along the top and left edges, both clipped to the panel.
      ctx.save();
      ctx.beginPath();
      ctx.rect(0, band, w, ph);
      ctx.clip();
      ctx.shadowColor = HOLE_SHADOW;
      ctx.shadowBlur = SHADOW_BLUR * size * k;
      ctx.shadowOffsetX = SHADOW_X * size * k;
      ctx.shadowOffsetY = SHADOW_Y * size * k;
      ctx.drawImage(front, 0, band);
      ctx.shadowColor = "transparent";
      ctx.drawImage(wall, WALL_X * size * k, band + WALL_Y * size * k);
      ctx.restore();
      ctx.drawImage(front, 0, band);
      // A faint lit rim on the edges.
      ctx.fillStyle = EDGE;
      ctx.fillRect(0, band, w, Math.max(1, Math.round(1.5 * k)));
      ctx.fillRect(0, band + ph - Math.max(1, Math.round(1.5 * k)), w, Math.max(1, Math.round(1.5 * k)));
      lastY = NaN;
    }

    function setShown(on) {
      if (on === shownNow) return;
      shownNow = on;
      panel.style.visibility = on ? "visible" : "hidden";
      if (on && !watching) {
        watching = true;
        ticks = 0;
        requestAnimationFrame(watch);
      }
    }

    // Hides the panel once WebGL frames stop arriving.
    function watch() {
      if (!shownNow) {
        watching = false;
        return;
      }
      if (++ticks > STALE_TICKS) {
        watching = false;
        setShown(false);
        return;
      }
      requestAnimationFrame(watch);
    }

    function undo() {
      root.classList.remove("sw-block-3d");
      panel?.remove();
    }

    const stop = SW.onFrame(() => {
      try {
        ticks = 0;
        title ||= document.querySelector("h2.sw-block-title");
        if (!title) return;
        if (!panel) build();
        const shown = shownAmount();
        if (shown <= 0.001) {
          setShown(false);
          return;
        }
        draw();
        // The title's own opacity curve is already eased by the site.
        const y = Math.round((dir < 0 ? travel[0] : -travel[1]) * (1 - shown) * 10) / 10;
        if (y !== lastY) {
          lastY = y;
          panel.style.transform = `translate3d(0, ${y}px, 0)`;
        }
        setShown(true);
      } catch (err) {
        // Put the page back to the DOM title; SWText logs the error.
        stop();
        undo();
        throw err;
      }
    });
  });
})();
