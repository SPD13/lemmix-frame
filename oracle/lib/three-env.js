"use strict";
/**
 * The 3D layer's data side under node: three.js (the UMD build the pages load), then depth.js,
 * decals.js and terrain.js run as the browser runs them - classic scripts sharing one global
 * scope - so the oracle drives the very classes the diorama uses. Nothing is rendered: only
 * BufferGeometry / DataTexture data is read back.
 *
 * Also the two glue pieces of app.js the terrain needs, copied as they are (app.js is a page
 * script that cannot be loaded): groundData (app.js:5874) and the profile load (app.js:2887,
 * ProfileStore.urlsForGroundData + ProfileFiles.loadAll over 3d/profiles/).
 */
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { WEB } = require("./env");

globalThis.THREE = require(path.join(WEB, "3d", "lib", "three.min.js"));
for (const name of ["depth.js", "decals.js", "terrain.js"]) {
  const file = path.join(WEB, "3d", "js", name);
  vm.runInThisContext(fs.readFileSync(file, "utf8"), { filename: file });
}
const G = vm.runInThisContext(`({
  DepthClass, DEPTH_BANDS, RELIEF_TOP, buildDepthMap, buildPieceMap, buildReliefMap, buildBlendMap,
  buildColorBlendMap, TerrainMesh, TerrainDecals, TERRAIN_CHUNK,
})`);
const { ProfileStore, ProfileFiles } = require(path.join(WEB, "3d", "js", "profile-store.js"));

/** app.js lemmixEngine.groundData, verbatim. */
function groundData(level) {
  const ids = new Map();
  const terraImages = {};
  const terrains = [];
  for (const p of level.pieces || []) {
    const d = p.drawn;
    if (!ids.has(d.variantKey)) {
      const id = ids.size;
      ids.set(d.variantKey, id);
      const frame = new Uint8Array(d.width * d.height);
      for (let i = 0, a = 3; i < frame.length; i++, a += 4) frame[i] = d.image.data[a] < 128 ? 0x80 : 0;
      terraImages[id] = { width: d.width, height: d.height, frames: [frame], name: d.key };
    }
    terrains.push({
      x: p.x, y: p.y, id: ids.get(d.variantKey), key: d.key,
      drawProperties: { isUpsideDown: false, noOverwrite: p.noOverwrite, onlyOverwrite: false, isErase: p.erase },
    });
  }
  return { lr: { levelWidth: level.width, levelHeight: level.height, terrains, graphicSet1: null, steel: [] }, terraImages };
}

/** The merged profile of a level's styles, read from web/3d/profiles as the page fetches it. */
async function loadProfile(gd) {
  const files = new ProfileFiles({
    fetch: async (url) => {
      const file = path.join(WEB, url);
      if (!fs.existsSync(file)) return { ok: false, status: 404 };
      const text = fs.readFileSync(file, "utf8");
      return { ok: true, json: async () => JSON.parse(text) };
    },
  });
  const urls = ProfileStore.urlsForGroundData(gd, null);
  return { urls, profile: await files.loadAll(urls) };
}

/** A SessionResources stand-in: the terrain only asks it to track what it creates. */
const resources = { track: (x) => x };

module.exports = { ...G, groundData, loadProfile, resources, ProfileStore };
