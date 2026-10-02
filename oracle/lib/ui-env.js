"use strict";
/**
 * The web engine as the 3D page has it, for the 2D pixel oracles (sprites, panel, minimap,
 * cursor): the classic js/lemmings.js loaded first (Lemmings.Frame, DisplayImage, EventHandler,
 * CommandManager and the Command* classes, as the page loads it before the Lemmix modules),
 * then the Lemmix engine (lib/env.js), game.js and panel.js. The browser pieces they touch are
 * stood in for:
 *   - setInterval: the GameTimer's interval never fires; the oracle ticks by hand
 *   - performance.now: a clock the oracle sets (the panel's hold-repeat)
 *   - document.createElement("canvas") / Image / THREE: a minimal 2D canvas over RGBA bytes,
 *     enough for minimap.js and cursor.js. drawImage is source-over for opaque and transparent
 *     pixels only (what the assets have); a translucent pixel is an error, since a browser's
 *     premultiplied blending is not something the port can claim to match.
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "web");

// the classic engine first, as the page loads it
const { classicEngine } = require(path.join(WEB, "tools", "classic-node.js"));
globalThis.Lemmings = classicEngine(WEB);

const env = require("./env");
const { Lemmix, nodeIO, ASSETS } = env;

// GameTimer.continue() takes an interval id; nothing ever fires
let intervalIds = 0;
globalThis.setInterval = () => ++intervalIds;
globalThis.clearInterval = () => {};

// the panel's hold-repeat reads performance.now()
const clock = { now: 0 };
Object.defineProperty(globalThis, "performance", { value: { now: () => clock.now }, configurable: true, writable: true });

for (const name of ["game.js", "panel.js"]) require(path.join(WEB, "lemmix", "js", name));

/**
 * The page's io (styles.js browserIO): a browser decodes a PNG with bytes after its IEND chunk
 * (Lemmings Plus III/IV/Omega/Omega II/V ship skill_count_digits.png so), where pngjs refuses
 * it - so the PNG is cut after IEND and decoded again, as the browser sees it.
 */
function pageIO(root) {
  const base = nodeIO(root);
  const { PNG } = require("pngjs");
  return {
    text: base.text,
    async image(url) {
      try { return await base.image(url); } catch (e) {
        if (!/end of stream/.test(String(e && e.message))) throw e;
        const buf = fs.readFileSync(path.join(root, decodeURIComponent(url)));
        const end = buf.indexOf("IEND");
        const png = PNG.sync.read(buf.subarray(0, end + 8));
        return new Lemmix.Bitmap(png.width, png.height, new Uint8ClampedArray(png.data.buffer, png.data.byteOffset, png.data.length));
      }
    },
  };
}
Lemmix.io = pageIO(ASSETS);

// ---------------------------------------------------------------- the canvas stand-in

function parseColor(s) {
  s = String(s).trim();
  let m = /^#([0-9a-f])([0-9a-f])([0-9a-f])$/i.exec(s);
  if (m) return [parseInt(m[1] + m[1], 16), parseInt(m[2] + m[2], 16), parseInt(m[3] + m[3], 16), 255];
  m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(s);
  if (m) return [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16), 255];
  throw new Error("colour not handled by the canvas stand-in: " + s);
}

class MockContext {
  constructor(canvas) { this.canvas = canvas; this.fillStyle = "#000"; this.imageSmoothingEnabled = true; }
  createImageData(w, h) { return { width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }; }
  putImageData(img, x, y) {
    const c = this.canvas;
    for (let sy = 0; sy < img.height; sy++) for (let sx = 0; sx < img.width; sx++) {
      const dx = x + sx, dy = y + sy;
      if (dx < 0 || dy < 0 || dx >= c.width || dy >= c.height) continue;
      const s = (sy * img.width + sx) * 4, d = (dy * c.width + dx) * 4;
      for (let k = 0; k < 4; k++) c.data[d + k] = img.data[s + k];
    }
  }
  getImageData(x, y, w, h) {
    const img = this.createImageData(w, h), c = this.canvas;
    for (let sy = 0; sy < h; sy++) for (let sx = 0; sx < w; sx++) {
      const s = ((y + sy) * c.width + x + sx) * 4, d = (sy * w + sx) * 4;
      for (let k = 0; k < 4; k++) img.data[d + k] = c.data[s + k];
    }
    return img;
  }
  fillRect(x, y, w, h) {
    const [r, g, b, a] = parseColor(this.fillStyle), c = this.canvas;
    const x0 = Math.max(0, x), y0 = Math.max(0, y), x1 = Math.min(c.width, x + w), y1 = Math.min(c.height, y + h);
    for (let yy = y0; yy < y1; yy++) for (let xx = x0; xx < x1; xx++) {
      const p = (yy * c.width + xx) * 4;
      c.data[p] = r; c.data[p + 1] = g; c.data[p + 2] = b; c.data[p + 3] = a;
    }
  }
  /** drawImage(src, dx, dy[, dw, dh]): nearest-neighbour when scaled (imageSmoothingEnabled is off where it is used). */
  drawImage(src, dx, dy, dw, dh) {
    if (!src) throw new Error("drawImage of nothing");
    const sw = src.width, sh = src.height;
    if (dw === undefined) { dw = sw; dh = sh; }
    if ((dw !== sw || dh !== sh) && this.imageSmoothingEnabled) throw new Error("scaled drawImage with smoothing");
    const c = this.canvas, sd = src.data;
    for (let y = 0; y < dh; y++) {
      const ty = dy + y;
      if (ty < 0 || ty >= c.height) continue;
      const sy = Math.floor((y + 0.5) * sh / dh);
      for (let x = 0; x < dw; x++) {
        const tx = dx + x;
        if (tx < 0 || tx >= c.width) continue;
        const sx = Math.floor((x + 0.5) * sw / dw);
        const s = (sy * sw + sx) * 4, d = (ty * c.width + tx) * 4;
        const a = sd[s + 3];
        if (a === 0) continue;
        if (a !== 255) throw new Error("translucent pixel in drawImage");
        c.data[d] = sd[s]; c.data[d + 1] = sd[s + 1]; c.data[d + 2] = sd[s + 2]; c.data[d + 3] = 255;
      }
    }
  }
}

const canvases = []; // every canvas made, in order (cursor.js keeps only some of them)
class MockCanvas {
  constructor() { this._w = 300; this._h = 150; this.data = new Uint8ClampedArray(this._w * this._h * 4); this._ctx = null; canvases.push(this); }
  get width() { return this._w; }
  set width(v) { this._w = v; this.data = new Uint8ClampedArray(this._w * this._h * 4); }
  get height() { return this._h; }
  set height(v) { this._h = v; this.data = new Uint8ClampedArray(this._w * this._h * 4); }
  getContext() { return this._ctx || (this._ctx = new MockContext(this)); }
  toDataURL() { return "data:mock," + canvases.indexOf(this); }
}

/** Image: `src` is read from the asset root through pngjs, onload/onerror on the next microtask. */
const missingImages = new Set(); // asset-relative paths the oracle pretends are absent
class MockImage {
  constructor() { this.onload = null; this.onerror = null; this.width = 0; this.height = 0; this.data = null; }
  set src(url) {
    this._src = url;
    Promise.resolve().then(async () => {
      const bmp = missingImages.has(url) ? null : await nodeIO(ASSETS).image(url);
      if (!bmp) { if (this.onerror) this.onerror(); return; }
      this.width = bmp.width; this.height = bmp.height; this.data = bmp.data;
      if (this.onload) this.onload();
    });
  }
  get src() { return this._src; }
}

class Obj3 {
  constructor() {
    this.position = { x: 0, y: 0, z: 0, set(x, y, z) { this.x = x; this.y = y; this.z = z; } };
    this.scale = { x: 1, y: 1, z: 1, set(x, y, z) { this.x = x; this.y = y; this.z = z; } };
  }
}
const THREE = {
  NearestFilter: 1003,
  CanvasTexture: class { constructor(c) { this.image = c; } },
  PlaneGeometry: class {},
  MeshBasicMaterial: class { constructor(o) { Object.assign(this, o); } },
  SpriteMaterial: class { constructor(o) { Object.assign(this, o); } },
  Mesh: class extends Obj3 { constructor(g, m) { super(); this.geometry = g; this.material = m; } },
  Sprite: class extends Obj3 { constructor(m) { super(); this.material = m; } },
};

globalThis.document = { createElement: (tag) => { if (tag !== "canvas") throw new Error("createElement " + tag); return new MockCanvas(); } };
globalThis.Image = MockImage;
globalThis.THREE = THREE;

/** A 3d/js script (a plain browser script) with its top-level class exported to globalThis. */
function loadScript(rel, names) {
  const src = fs.readFileSync(path.join(WEB, rel), "utf8");
  vm.runInThisContext(src + "\n;" + names.map((n) => "globalThis." + n + " = " + n + ";").join(""), { filename: rel });
}

// ---------------------------------------------------------------- hashing helpers

/** A Lemmings.Frame (or null): size, offset, pixels, mask. */
function hashFrameInto(h, frame) {
  if (!frame) return h.word(0x7fffffff);
  h.num(frame.width, "w"); h.num(frame.height, "h"); h.num(frame.offsetX, "ox"); h.num(frame.offsetY, "oy");
  h.bytes(frame.data); h.bytes(frame.mask);
}

function hashBitmapInto(h, bmp) {
  if (!bmp) return h.word(0x7fffffff);
  h.num(bmp.width, "w"); h.num(bmp.height, "h"); h.bytes(bmp.data);
}

/** The levels of the index with the pack each belongs to ({level, pack}); pack null for none. */
function levelsWithPacks() {
  const index = JSON.parse(fs.readFileSync(path.join(ASSETS, "levels", "index.json"), "utf8"));
  const out = [];
  const walk = (node, pack) => {
    if (node.kind === "pack") pack = node;
    for (const level of node.levels || []) if (level.url) out.push({ level, pack });
    for (const child of node.children || []) walk(child, pack);
  };
  walk(index, null);
  return out;
}

module.exports = { ...env, Lemmings: globalThis.Lemmings, clock, canvases, missingImages, loadScript, hashFrameInto, hashBitmapInto, levelsWithPacks };
