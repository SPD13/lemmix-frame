#!/usr/bin/env node
"use strict";
/**
 * The replay-markers oracle (web/3d/js/replay-markers.js): for every solved level in
 * web/solutions, the markers ReplayMarkers builds from the stored solution - with a few
 * release-rate changes and a nuke added, so the hatch markers are there too - and how they
 * stand at a handful of frames: every plane's place, size and draw order, each picture's pixels
 * (the outlined skill and panel icons, as written into the canvas before it is uploaded), the
 * label texts and how their canvases are laid out (fillRect / fillText calls: the glyphs are the
 * browser's), and each frame's opacities, countdown seconds and visibility.
 * A stand-in canvas records what the class draws; nothing is rendered. Hashes only:
 * oracle/out/markers.json.
 *   node oracle/markers.js [prefix]
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const env = require("./lib/env");
const { StateHash } = require("./lib/hash");
const S = require("./lib/scene-env");

S.load();
const { Lemmix } = env;
require(path.join(env.WEB, "lemmix", "js", "panel.js"));

// a canvas that keeps what is drawn on it: the ImageData put, the fill and text calls
global.document = {
  createElement(tag) {
    if (tag !== "canvas") throw new Error("createElement " + tag);
    const cv = { width: 300, height: 150, imageData: null, ops: [] };
    const ctx = {
      fillStyle: "#000", font: "10px sans-serif", textAlign: "start", textBaseline: "alphabetic",
      createImageData: (w, h) => ({ width: w, height: h, data: new Uint8ClampedArray(w * h * 4) }),
      putImageData: (img, x, y) => { cv.imageData = img; cv.ops.push(["putImageData", x, y]); },
      fillRect: (x, y, w, h) => cv.ops.push(["fillRect", ctx.fillStyle, x, y, w, h]),
      fillText: (text, x, y) => cv.ops.push(["fillText", ctx.fillStyle, ctx.font, ctx.textAlign, ctx.textBaseline, text, x, y]),
    };
    cv.getContext = () => ctx;
    return cv;
  },
};
vm.runInThisContext(fs.readFileSync(path.join(env.WEB, "3d", "js", "replay-markers.js"), "utf8"), { filename: "replay-markers.js" });
const ReplayMarkers = global.ReplayMarkers;
const Z = S.load().TERRAIN_DEPTH / 2 - S.load().SPRITE_DEPTH / 2 + 2; // app.js: LEMMING_Z + 2

const F = (h, v) => S.hashF64(h, v);

function hashCanvas(h, cv) {
  h.word(cv.width); h.word(cv.height);
  if (cv.imageData) h.bytes(cv.imageData.data); else h.word(0x7fffffff);
  h.word(cv.ops.length);
  for (const op of cv.ops) { h.word(op.length); for (const v of op) if (typeof v === "string") h.str(v); else F(h, v); }
}

/** The synthetic entries every run gets on top of its solution: two release-rate changes and a nuke. */
function extraEntries(level) {
  return [
    { type: "spawn_interval", frame: 40, interval: level.spawnInterval - 10, spawned: 0 },
    { type: "spawn_interval", frame: 40, interval: level.spawnInterval + 4, spawned: 3 },
    { type: "spawn_interval", frame: 300, interval: level.spawnInterval + 7, spawned: 200 },
    { type: "nuke", frame: 777 },
  ];
}

function hashObject(h, o) {
  F(h, o.position.x); F(h, o.position.y); F(h, o.position.z); F(h, o.scale.y); h.word(o.renderOrder); h.word(o.visible ? 1 : 0);
  const p = o.geometry.parameters;
  if (o.geometry.type === "PlaneGeometry") { h.str("plane"); F(h, p.width); F(h, p.height); }
  else { h.str(o.geometry.type); S.hashGeometry(h, o.geometry); }
}

async function main() {
  const prefix = process.argv[2];
  const io = env.nodeIO(env.ASSETS);
  const styles = new Lemmix.StyleManager(io);
  const masks = await Lemmix.loadMasks(io);
  const assets = await Lemmix.loadPanelAssets(io, null);
  const index = JSON.parse(fs.readFileSync(path.join(env.WEB, "solutions", "index.json"), "utf8"));
  const all = env.listLevels(env.ASSETS);
  const t0 = Date.now();
  const rows = [];
  const iconHashes = {};
  for (const [id, sol] of Object.entries(index.levels)) {
    if (sol.status !== "solved" || (prefix && !id.startsWith(prefix))) continue;
    const entry = all.find((l) => l.id === id);
    if (!entry) continue;
    const row = { id, url: entry.url, nxrp: path.join("solutions", sol.file) };
    try {
      const level = await S.buildLevel(styles, entry);
      const sim = new Lemmix.LemGame(level, masks);
      sim.loadReplay(Lemmix.Replay.parse(fs.readFileSync(path.join(env.WEB, row.nxrp), "utf8")));
      for (const e of extraEntries(level)) sim.recorded.push(e);
      const setName = (level.theme && level.theme.lemmings) || "default";
      const sprites = await new Lemmix.SpriteSet(io).load(setName);
      const panel = { icons: {}, game: { sprites }, assets };
      const gui = { assets, _skillIcon: (name) => Lemmix.GamePanel.prototype._skillIcon.call(panel, name) };
      const game = { sim, gui, replayEngaged: true };
      const markers = new ReplayMarkers({ THREE, worldGroup: new THREE.Group(), level, game, z: Z });
      const h = new StateHash();
      const last = Math.max(...sim.recorded.map((r) => r.frame));
      const steps = [[0, 0], [40, 250], [41, 1000], [Math.floor(last / 2), 3333.3], [last, 77], [last + 1, 5]];
      steps.forEach(([frame, now], k) => {
        sim.currentIteration = frame;
        markers.hidden = k === 2; // the pictures put away (clear physics) on one step
        markers.update(now);
        if (k === 0) {
          // the built markers: every plane, its pictures
          S.hashGeometry(h, markers.ringGeometry);
          h.word(markers.markers.length);
          for (const m of markers.markers) {
            h.word(m.frame); h.str(m.entry.type); h.str(m.textOnly);
            h.word(m.objects.length);
            for (const o of m.objects) {
              hashObject(h, o);
              const map = o.material.map;
              if (map && map.image) hashCanvas(h, map.image); else h.word(0x7fffffff);
            }
            F(h, m.iconAt.x); F(h, m.iconAt.y);
          }
          h.word(markers.textures.size);
          for (const [key, e] of markers.textures) {
            h.str(key); h.word(e.w); h.word(e.h);
            const c = new StateHash(); hashCanvas(c, e.texture.image); iconHashes[setName + "/" + key] = c.hex();
          }
        }
        // the state at this frame
        h.word(markers.group.visible ? 1 : 0);
        for (const m of markers.markers) {
          for (const mat of m.materials) F(h, mat.opacity);
          h.word(m.label.visible ? 1 : 0); h.num(m.labelSeconds, "seconds"); F(h, m.label.material.opacity);
          if (m.label.material.map) hashCanvas(h, m.label.material.map.image); else h.word(0x7fffffff);
        }
      });
      row.markers = markers.markers.length;
      row.hash = h.hex();
    } catch (e) { row.error = String(e && e.stack || e); }
    rows.push(row);
  }
  const file = path.join(env.OUT, "markers.json");
  fs.writeFileSync(file, JSON.stringify({ webSha: S.webSha(), z: Z, icons: iconHashes, levels: rows }).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} solutions, ${rows.reduce((a, r) => a + (r.markers || 0), 0)} markers, ${Object.keys(iconHashes).length} pictures, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
