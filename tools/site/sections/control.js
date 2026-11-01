// Control section: "Full Control" as extruded 3D letters on the terrain,
// with frosted app cards (an app name and its Allow or Block rule) floating
// around them at a few depths.
//
// Built with window.SWText (tools/site/lib/swtext.js). The letters and cards
// follow the Control title's own show/hide: the lowest computed opacity of the
// h2, its ancestors and its descendants. They rise with it as it shows and
// sink behind it as it hides. As soon as the section is set up, <html> gets
// sw-control-3d and control.css makes the DOM h2 transparent, so the flat
// heading never shows first; it keeps its text and stays in the
// accessibility tree. The cards are decorative (aria-hidden) and are added
// only from onTerrain, so if SWText fails to set up, the page keeps its
// original heading and gets nothing.
//
// The per-frame work runs only while the site renders WebGL, and the site
// stops rendering past .js-webgl-scroll-end. A quick scroll can get there
// before the cards have faded, so while they show, a timer checks that frames
// still arrive and hides them when frames stop.
(() => {
  "use strict";

  // Card content: placeholder app names, each with one rule. Order sets the
  // slot each card takes in control.css (data-sw-card="0".."5").
  const CARDS = [
    { name: "Web browser", rule: "allow" },
    { name: "Game launcher", rule: "allow" },
    { name: "Telemetry", rule: "block" },
    { name: "Mail", rule: "allow" },
    { name: "Updater", rule: "block" },
    { name: "Sync service", rule: "block" },
  ];

  const SW = window.SWText;
  if (!SW) return;

  // Mesh-local z. In this section the scroll camera comes down into the
  // terrain while the title shows (z about 0.88 to 0.63, looking almost
  // straight down with a very wide lens), so a fixed height would either
  // pass the camera's near plane or sit under the terrain's outer rings.
  // The letters' face is kept GAP below the camera, re-placed as it moves,
  // so they keep one size and depth on screen; the stack reaches DEPTH down.
  // No terrain mound: with the camera this close, raising the terrain under
  // the letters stacked its layers into a striped grey cliff between the two
  // lines, so the letters stand over the site's own white well instead.
  const GAP = 0.2;
  const DEPTH = 0.075;
  const SLICES = 56;
  // Face shading from the mask's edge gradient. Kept low so the faces stay
  // flat and the edges crisp under the site's post-process blur.
  const BEVEL = 0.8;
  // Smallest margin (px) between the letters and the viewport's sides. The
  // post-process fringes and blurs most near the sides, so on a phone the
  // title is kept well inside them.
  const EDGE = 40;
  // Slate-grey sides, darkening toward the base, so the depth reads against
  // the white face and the white terrain.
  const SIDES = [0.42, 0.47, 0.56];
  // Sink speed (per second) toward the title's visible state. On the way up
  // the letters follow the title's opacity, so they stand while it shows.
  const SPEED = 3.5;
  // Frames stopped (ms) before the cards are hidden; the timer that checks
  // runs every STALL / 2 while the cards show.
  const STALL = 200;

  function buildCards() {
    const box = document.createElement("div");
    box.className = "sw-control-cards";
    box.setAttribute("aria-hidden", "true");
    box.dataset.swSection = "control";
    CARDS.forEach((card, i) => {
      const el = document.createElement("div");
      el.className = "sw-control-card";
      el.dataset.swSection = "control";
      el.dataset.swCard = String(i);
      el.dataset.rule = card.rule;
      const mark = document.createElement("span");
      mark.className = "sw-control-card__mark";
      const text = document.createElement("span");
      text.className = "sw-control-card__text";
      const name = document.createElement("span");
      name.className = "sw-control-card__name";
      name.textContent = card.name;
      const rule = document.createElement("span");
      rule.className = "sw-control-card__rule";
      rule.textContent = card.rule === "allow" ? "Allow" : "Block";
      // The space keeps the name and rule separate words in textContent;
      // a whitespace-only text node in the flex column draws nothing.
      text.append(name, " ", rule);
      el.append(mark, text);
      box.append(el);
    });
    document.body.append(box);
    return box;
  }

  // The title's visible state: the lowest opacity among the h2, its
  // ancestors and its descendants; 0 if any is hidden or not displayed.
  function visibleState(chain) {
    let min = 1;
    for (const el of chain) {
      const css = getComputedStyle(el);
      if (css.display === "none" || css.visibility === "hidden") return 0;
      const o = parseFloat(css.opacity);
      if (o < min) min = o;
    }
    return min;
  }

  // Adds the class that makes the DOM heading transparent, at once: the
  // stylesheet's color transition is for handing the page back to the DOM
  // heading, not for hiding it.
  function hideHeading(el, cls) {
    const old = el.style.transition;
    el.style.transition = "none";
    document.documentElement.classList.add(cls);
    getComputedStyle(el).color; // applies the class before the transition returns
    el.style.transition = old;
  }

  SW.onTerrain(layer => {
    let title = null;
    let chain = null;
    let descendants = -1;
    let subtree = null;
    let mask = null;
    let text = null;
    let cards = null;
    let placed = false;
    let rise = 0;
    let lastShown = 0;
    let shownCards = -1;
    let stop = null;
    // Frame watchdog for the cards (see the header).
    let lastFrame = 0;
    let watch = 0;
    let stalled = false;
    // Camera state at the last place(), to re-place only when it moves.
    // (The scroll animates both its pose and its field of view.)
    const lastCam = new Float64Array(32);

    function camMoved(camera) {
      const e = camera.matrixWorld.elements;
      const p = camera.projectionMatrix.elements;
      let moved = false;
      for (let i = 0; i < 16; i++) {
        if (Math.abs(e[i] - lastCam[i]) > 1e-6 || Math.abs(p[i] - lastCam[16 + i]) > 1e-6) moved = true;
        lastCam[i] = e[i];
        lastCam[16 + i] = p[i];
      }
      return moved;
    }

    const lastBox = new Float64Array(4);
    function boxMoved(box) {
      const moved =
        Math.abs(box.left - lastBox[0]) > 0.25 || Math.abs(box.top - lastBox[1]) > 0.25 ||
        Math.abs(box.width - lastBox[2]) > 0.25 || Math.abs(box.height - lastBox[3]) > 0.25;
      lastBox[0] = box.left;
      lastBox[1] = box.top;
      lastBox[2] = box.width;
      lastBox[3] = box.height;
      return moved;
    }

    // Builds the letters and cards and hides the DOM heading. false while
    // the heading is not in the page yet (onFrame tries again).
    function setup() {
      const el = document.querySelector("h2.sw-control-title");
      if (!el) return false;
      title = el;
      mask = SW.glyphMask(title, { pad: 0.12, edge: 0.05 });
      text = new SW.SliceText(layer, {
        mask,
        slices: SLICES,
        baseZ: 1 - DEPTH,
        topZ: 1,
        face: [1, 1, 1],
        sides: SIDES,
        bevel: BEVEL,
      });
      text.mesh.name = "sw-control";
      cards = buildCards();
      hideHeading(title, "sw-control-3d");
      return true;
    }

    function showCards(step) {
      shownCards = step;
      cards.style.setProperty("--sw-show", String(step));
      cards.classList.toggle("is-shown", step > 0);
    }

    // While the cards show: hides them once frames have stopped for STALL
    // ms, else checks again. Not running while they are hidden.
    function watchFrames() {
      watch = 0;
      if (shownCards <= 0) return;
      if (performance.now() - lastFrame > STALL) {
        stalled = true;
        showCards(0);
      } else {
        watch = setTimeout(watchFrames, STALL / 2);
      }
    }

    // Puts the page back to the DOM heading; SWText logs the error.
    function fail(err) {
      stop?.();
      clearTimeout(watch);
      document.documentElement.classList.remove("sw-control-3d");
      cards?.remove();
      text?.dispose();
      throw err;
    }

    stop = SW.onFrame(({ renderer, camera, dt, now }) => {
      try {
        lastFrame = now;
        if (!title && !setup()) return;
        // GSAP splits the heading into word wrappers after the first render:
        // rebuild the chain when the heading's subtree changes.
        subtree ||= title.getElementsByTagName("*"); // live collection
        if (!chain || subtree.length !== descendants) {
          descendants = subtree.length;
          chain = [title, ...subtree];
          for (let p = title.parentElement; p && p !== document.documentElement; p = p.parentElement) chain.push(p);
        }
        const shown = visibleState(chain);
        const rising = shown > lastShown;
        lastShown = shown;
        // Place while the title is fully shown, and on every frame it comes
        // in, so the letters stand as it does; again whenever the camera or
        // the heading's box moves.
        if ((shown > 0.99 || (rising && shown > 0)) && mask.update()) {
          const cam = camMoved(camera);
          const box = boxMoved(mask.box);
          if (cam || box || !placed) {
            // SliceText reads topZ and baseZ on every place() and setAmount().
            const top = camera.matrixWorld.elements[14] - GAP;
            text.topZ = top;
            text.baseZ = top - DEPTH;
            // On narrow screens the DOM title runs past the viewport edges:
            // shrink the box the letters are placed on (mask.box, which
            // place() reads and the next update() resets) about its centre
            // so the whole title fits with a margin.
            const real = mask.box;
            const fit = Math.min(1, (innerWidth - 2 * EDGE) / title.getBoundingClientRect().width);
            if (fit < 1) {
              const w = real.width * fit;
              const h = real.height * fit;
              mask.box = new DOMRect(real.x + (real.width - w) / 2, real.y + (real.height - h) / 2, w, h);
            }
            if (text.place(camera, renderer)) placed = true;
            mask.box = real;
          }
        }
        const target = placed ? shown : 0;
        // Back from a stall (rendering stopped and resumed): no higher than
        // the title is now, so nothing shows that it hides.
        if (stalled) {
          stalled = false;
          rise = Math.min(rise, target);
        }
        // Up: with the title's opacity. Down: sink at SPEED.
        rise = target > rise ? target : rise + (target - rise) * Math.min(1, dt * SPEED);
        if (rise < 0.001) rise = 0;
        const eased = rise * rise * (3 - 2 * rise);
        // place() rewrites the stack itself; otherwise only on a change.
        if (eased !== text.amount) text.setAmount(eased);
        // Cards follow the same eased amount, in steps of 1/100.
        const step = Math.round(eased * 100) / 100;
        if (step !== shownCards) showCards(step);
        if (shownCards > 0 && !watch) watch = setTimeout(watchFrames, STALL / 2);
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
