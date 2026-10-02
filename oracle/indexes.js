#!/usr/bin/env node
"use strict";
/**
 * The index oracle: levels/index.json, neolemmix/styles/index.json and neolemmix/music/index.json
 * as the web's builders make them for the installed assets (no classic packs: config = []), with
 * `generated` blanked. The full JSON goes to oracle/out/index/ (not committed: it lists the
 * assets); oracle/out/indexes.json keeps their hashes. Also a collation check: random file-like
 * names sorted by localeCompare(numeric, base), for the port's NaturalCompare.
 *   node oracle/indexes.js
 */
const fs = require("fs");
const path = require("path");
const { ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const LevelsIndex = require(path.join(WEB, "tools", "levels-index.js"));
const StylesIndex = require(path.join(WEB, "tools", "styles-index.js"));
const MusicIndex = require(path.join(WEB, "tools", "music-index.js"));

const strip = (o) => { o.generated = ""; return o; };
const hashOf = (text) => { const h = new StateHash(); h.str(text); return h.hex(); };

const io = LevelsIndex.nodeIO(ASSETS);
const out = {
  levels: strip(LevelsIndex.buildIndex(io, [])),
  styles: strip(StylesIndex.buildStylesIndex(io)),
  music: strip(MusicIndex.buildMusicIndex(io)),
};
fs.mkdirSync(path.join(OUT, "index"), { recursive: true });
const hashes = {};
for (const [k, v] of Object.entries(out)) {
  const text = JSON.stringify(v);
  fs.writeFileSync(path.join(OUT, "index", k + ".json"), text);
  hashes[k] = hashOf(text);
}

// collation: names built from pieces that exercise case, digits, leading zeros, punctuation, accents
let seed = 12345;
const rnd = () => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return seed / 4294967296; };
const parts = ["a", "A", "b", "Z", "z", "_", "-", " ", ".", "'", "!", "(", ")", "0", "00", "1", "01", "2", "9", "10", "100", "007", "é", "E", "x1y", "Level", "level", "&", "+", "~", "$", "#"];
const names = [];
for (let i = 0; i < 3000; i++) {
  let s = "";
  const n = 1 + Math.floor(rnd() * 5);
  for (let k = 0; k < n; k++) s += parts[Math.floor(rnd() * parts.length)];
  names.push(s);
}
const natural = (a, b) => a.localeCompare(b, "en", { numeric: true, sensitivity: "base" });
const pairs = [];
for (let i = 0; i < 6000; i++) {
  const a = names[Math.floor(rnd() * names.length)], b = names[Math.floor(rnd() * names.length)];
  pairs.push([a, b, Math.sign(natural(a, b))]);
}
fs.writeFileSync(path.join(OUT, "indexes.json"), JSON.stringify({ webSha: require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim(), hashes, collation: pairs }) + "\n");
console.log(hashes, out.levels.count + " levels, " + out.styles.count + " styles, " + out.music.count + " music files");
