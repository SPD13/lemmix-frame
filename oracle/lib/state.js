"use strict";
/**
 * The canonical per-frame state of a LemGame, fed into a StateHash in a fixed order that the
 * C# port reproduces (Lemmix.Core Oracle/SimState.cs). The order is written to
 * oracle/out/sim/schema.json so both sides read it from one place.
 */
const { StateHash } = require("./hash");

function schemaOf(Lemmix, game) {
  const scalars = Object.keys(game.saveState({ physicsOnly: true }).scalars);
  const L = new Lemmix.Lemming(0);
  const lemming = Object.keys(L).filter((k) => k !== "game");
  return { scalars, lemming, lemmingExtra: LEMMING_EXTRA,
    gadget: ["remainingLemmings", "holdActive", "triggered", "secondariesTreatAsBusy", "teleLem", "zombieMode", "neutralMode", "x", "y", "effect"],
    animation: ["frame", "state", "visible"] };
}

// fields lemgame.js adds to a lemming outside its constructor, hashed after the schema's
// (absent = null): handleLasering sets laserHitPoint = [x, y]
const LEMMING_EXTRA = ["laserHitPoint"];

const sortedNumKeys = (o) => Object.keys(o).map(Number).sort((a, b) => a - b);

/** Hash of the frame's state without the terrain (cheap enough for every frame). */
function hashFrame(game, schema, withTerrain) {
  const h = new StateHash();
  for (const k of schema.scalars) h.any(game[k], k);
  for (const counts of [game.currSkillCount, game.usedSkillCount]) {
    const keys = sortedNumKeys(counts);
    h.word(keys.length);
    for (const k of keys) { h.num(k, "skillKey"); h.num(counts[k], "skillCount"); }
  }
  const tal = [...game.talismansAchieved].map(String).sort();
  h.word(tal.length); for (const t of tal) h.str(t);
  h.word(game.lemmings.length);
  for (const L of game.lemmings) {
    for (const k of schema.lemming) h.any(L[k], "lem." + k);
    for (const k of LEMMING_EXTRA) h.any(L[k], "lem." + k);
    // a field the constructor does not create would be missed silently: refuse it
    if (Object.keys(L).length !== schema.lemming.length) {
      const extra = Object.keys(L).filter((k) => k !== "game" && !schema.lemming.includes(k) && !LEMMING_EXTRA.includes(k));
      if (extra.length) throw new Error("lemming field outside the schema: " + extra.join(","));
    }
  }
  h.word(game.gadgets.length);
  for (const g of game.gadgets) {
    for (const k of schema.gadget) h.any(g[k], "gadget." + k);
    h.word(g.animations.length);
    for (const a of g.animations) for (const k of schema.animation) h.any(a[k], "anim." + k);
  }
  h.word(game.sounds.length);
  for (const s of game.sounds) { h.str(s.name); h.num(s.x, "sound.x"); h.num(s.y, "sound.y"); }
  h.word(game.recorded.length);
  if (withTerrain) hashTerrainInto(h, game.level);
  return h;
}

function hashTerrainInto(h, level) {
  h.bytes(level.physics);
  h.bytes(level.groundImage);
  h.bytes(level.groundMask.groundMask);
}

function terrainHash(level) { const h = new StateHash(); hashTerrainInto(h, level); return h.hex(); }

module.exports = { schemaOf, hashFrame, terrainHash };
