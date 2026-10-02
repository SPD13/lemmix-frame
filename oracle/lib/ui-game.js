"use strict";
/**
 * A Lemmix Game built the way the 3D page builds one (app.js lemmixEngine.createGame): the
 * level from the styles, the sprite set its theme names, the masks, and the pack's own panel
 * graphics when the levels index flags them. Shared by oracle/panel.js and oracle/minimap.js.
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, ASSETS, levelsWithPacks } = require("./ui-env");

/** The sample: every `step`-th level of the index, plus one level of each pack with its own panel. */
function sampleLevels(count, panelLevelIndex) {
  const all = levelsWithPacks();
  const step = Math.max(1, Math.floor(all.length / count));
  const picked = all.filter((_, i) => i % step === Math.floor(step / 2));
  const seenPacks = new Set();
  for (const e of all) {
    if (!e.pack || !e.pack.panel || seenPacks.has(e.pack)) continue;
    const ofPack = all.filter((x) => x.pack === e.pack);
    seenPacks.add(e.pack);
    const pick = ofPack[Math.min(panelLevelIndex, ofPack.length - 1)];
    if (!picked.includes(pick)) picked.push(pick);
  }
  return picked.map(({ level, pack }) => ({ id: level.id, url: level.url, packDir: pack && pack.dir && pack.panel ? pack.dir : null }));
}

class GameFactory {
  constructor() {
    this.io = nodeIO(ASSETS);
    this.styles = new Lemmix.StyleManager(this.io);
    this.masks = null;
    this.spriteSets = new Map();
  }
  async create(entry) {
    if (!this.masks) this.masks = await Lemmix.loadMasks(this.io);
    const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
    const level = await Lemmix.LevelBuilder.build(data, this.styles, { seed: entry.id });
    const setName = (level.theme && level.theme.lemmings) || "default";
    if (!this.spriteSets.has(setName)) this.spriteSets.set(setName, await new Lemmix.SpriteSet(this.io).load(setName));
    const game = new Lemmix.Game(level, { masks: this.masks, sprites: this.spriteSets.get(setName) });
    game.packDir = entry.packDir;
    return game;
  }
}

module.exports = { sampleLevels, GameFactory };
