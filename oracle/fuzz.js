#!/usr/bin/env node
"use strict";
/**
 * The input-fuzz oracle: a seeded random player per level - legal skill assignments, release
 * rate changes, now and then a nuke - written down as a script of API calls by frame
 * (assignSkillTo, adjustSpawnInterval, nuke). The script is then played on a fresh game and
 * hashed exactly as sim.js does, so the C# port replays the same calls and compares blocks.
 * The replay the game recorded is kept too (Replay.serialize), for the port's serializer.
 *   node oracle/fuzz.js [prefix] [--runs N] [--frames N]
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, listLevels, ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const { schemaOf, hashFrame, terrainHash } = require("./lib/state");

const BLOCK = 100, TERRAIN_EVERY = 17;

function mulberry32(a) {
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const seedOf = (s) => { let h = 2166136261; for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 16777619); return h >>> 0; };

async function build(env, entry) {
  const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
  const level = await Lemmix.LevelBuilder.build(data, env.styles, { seed: entry.id });
  return { level, game: new Lemmix.LemGame(level, env.masks) };
}

/** Play randomly; return the script of calls that took effect. */
function invent(game, rand, maxFrames) {
  game.start();
  const script = [];
  const skills = game.activeSkills;
  for (let f = 0; f < maxFrames && !game.gameFinished && !game.stateIsUnplayable; f++) {
    const at = game.currentIteration;
    if (skills.length && rand() < 1 / 20) {
      const skill = skills[Math.floor(rand() * skills.length)];
      const action = Lemmix.SKILL_TO_ACTION[skill];
      const cands = game.lemmings.filter((L) => !L.removed && !L.teleporting && L.portalWarpFrame === 0 && game.mayAssign(action, L) && game.checkSkillAvailable(action));
      if (cands.length) {
        const L = cands[Math.floor(rand() * cands.length)];
        if (game.assignSkillTo(L, skill)) script.push({ f: at, op: "assign", lem: L.index, skill });
      }
    }
    if (rand() < 1 / 150) {
      const si = game.currSpawnInterval + (rand() < 0.5 ? -1 : 1) * (1 + Math.floor(rand() * 10));
      const before = game.recorded.length;
      game.adjustSpawnInterval(si);
      if (game.recorded.length !== before || game.currSpawnInterval === si) script.push({ f: at, op: "si", si });
    }
    if (f > 400 && rand() < 1 / 2500 && !game.userSetNuking) { game.nuke(); script.push({ f: at, op: "nuke" }); }
    game.update();
  }
  return script;
}

/** The script on a fresh game, hashed as sim.js run() does. */
function play(game, level, script, maxFrames) {
  const builtTerrain = terrainHash(level);
  game.start();
  const schema = schemaOf(Lemmix, game);
  const build = hashFrame(game, schema, true);
  const blocks = [];
  let block = new StateHash();
  let f = 0, k = 0;
  while (f < maxFrames && !game.gameFinished && !game.stateIsUnplayable) {
    while (k < script.length && script[k].f === game.currentIteration) {
      const s = script[k++];
      if (s.op === "assign") game.assignSkillTo(game.lemmings[s.lem], s.skill);
      else if (s.op === "si") game.adjustSpawnInterval(s.si);
      else if (s.op === "nuke") game.nuke();
    }
    game.update();
    f++;
    const h = hashFrame(game, schema, f % TERRAIN_EVERY === 0);
    block.word(h.h1); block.word(h.h2);
    if (f % BLOCK === 0) { blocks.push(block.hex()); block = new StateHash(); }
  }
  const last = hashFrame(game, schema, true);
  block.word(last.h1); block.word(last.h2);
  blocks.push(block.hex());
  return { builtTerrain, build: build.hex(), frames: f, blocks, nxrp: Lemmix.Replay.serialize(game, {}),
    end: { out: game.lemmingsOut, saved: game.lemmingsIn, removed: game.lemmingsRemoved } };
}

async function main() {
  const args = process.argv.slice(2);
  const opt = (n, d) => { const i = args.indexOf(n); return i >= 0 ? +args[i + 1] : d; };
  const prefix = args.find((a, i) => !a.startsWith("--") && !(args[i - 1] || "").startsWith("--"));
  const runs = opt("--runs", 1), maxFrames = opt("--frames", 3000);
  const io = nodeIO(ASSETS);
  const env = { styles: new Lemmix.StyleManager(io), masks: await Lemmix.loadMasks(io) };
  const levels = listLevels(ASSETS, prefix);
  const rows = [];
  const t0 = Date.now();
  let actions = 0;
  for (const entry of levels) {
    for (let r = 0; r < runs; r++) {
      const row = { id: entry.id, url: entry.url, run: r };
      try {
        const rand = mulberry32(seedOf(entry.id + "#" + r));
        const script = invent((await build(env, entry)).game, rand, maxFrames);
        const { level, game } = await build(env, entry);
        Object.assign(row, { script }, play(game, level, script, maxFrames));
        actions += script.length;
      } catch (e) { row.error = String(e && e.message || e); }
      rows.push(row);
    }
    if (rows.length % 100 === 0) process.stderr.write(rows.length + " " + ((Date.now() - t0) / 1000).toFixed(0) + "s\n");
  }
  const sha = require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim();
  fs.mkdirSync(path.join(OUT, "sim"), { recursive: true });
  const file = path.join(OUT, "sim", "fuzz" + (prefix ? "-" + prefix.replace(/[^A-Za-z0-9]+/g, "_") : "") + ".json");
  fs.writeFileSync(file, JSON.stringify({ webSha: sha, mode: "fuzz", block: BLOCK, terrainEvery: TERRAIN_EVERY, maxFrames, levels: rows })
    .replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${rows.length} runs, ${actions} actions, ${rows.filter((r) => r.error).length} errors, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}
main().catch((e) => { console.error(e); process.exit(1); });
