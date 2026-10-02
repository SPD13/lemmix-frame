#!/usr/bin/env node
"use strict";
/**
 * The sim oracle: plays levels through the web engine and records a hash of the whole game
 * state every frame (oracle/lib/state.js), the terrain included every 17th frame and on the
 * last one. Frames are grouped in blocks of 100 whose hashes go into oracle/out/sim/<mode>.json,
 * small enough to commit; the C# tests replay the same inputs and compare block by block.
 *
 *   node oracle/sim.js noinput [prefix] [--frames N]   every level, no input (default cap 3000)
 *   node oracle/sim.js solutions [prefix]              the stored .nxrp solutions, as verify plays them
 *   node oracle/sim.js trace <level-id> [--nxrp f] [--frames N]   per-frame hashes of one run (debugging a mismatch)
 */
const fs = require("fs");
const path = require("path");
const { Lemmix, nodeIO, listLevels, ASSETS, WEB, OUT } = require("./lib/env");
const { StateHash } = require("./lib/hash");
const { schemaOf, hashFrame, terrainHash } = require("./lib/state");

const BLOCK = 100;
const TERRAIN_EVERY = 17;
const SOLUTION_MAX_FRAMES = 17 * 60 * 30; // tools/solver/verify.js MAX_FRAMES

async function setup() {
  const io = nodeIO(ASSETS);
  return { io, styles: new Lemmix.StyleManager(io), masks: await Lemmix.loadMasks(io) };
}

async function buildGame(env, entry) {
  const data = Lemmix.LevelBuilder.parseLevel(fs.readFileSync(path.join(ASSETS, entry.url), "utf8"));
  const level = await Lemmix.LevelBuilder.build(data, env.styles, { seed: entry.id });
  const game = new Lemmix.LemGame(level, env.masks);
  return { level, game };
}

/**
 * One run: start, optional replay, then update until the level ends or the cap. `nukeWhenOutOfTime`
 * is what the page (and verify.js) does when the clock runs out. Returns the record and, when
 * asked, the per-frame hashes.
 */
function run(game, level, { replay, maxFrames, nukeWhenOutOfTime, keepFrames }) {
  const builtTerrain = terrainHash(level); // the level as built, before the game touches it
  game.start();
  if (replay) game.loadReplay(replay);
  const schema = schemaOf(Lemmix, game);
  const build = hashFrame(game, schema, true);
  const blocks = [];
  const frames = keepFrames ? [] : null;
  let block = new StateHash();
  let nonInteger = build.nonInteger;
  let f = 0;
  while (f < maxFrames && !game.gameFinished && !game.stateIsUnplayable) {
    if (nukeWhenOutOfTime && game.isOutOfTime && !game.userSetNuking) game.nuke();
    game.update();
    f++;
    const h = hashFrame(game, schema, f % TERRAIN_EVERY === 0);
    if (h.nonInteger && !nonInteger) nonInteger = "frame " + f + ": " + h.nonInteger;
    if (frames) frames.push(h.hex());
    block.word(h.h1); block.word(h.h2);
    if (f % BLOCK === 0) { blocks.push(block.hex()); block = new StateHash(); }
  }
  // the last frame with its terrain, so a run ending between two terrain frames is still covered
  const last = hashFrame(game, schema, true);
  block.word(last.h1); block.word(last.h2);
  blocks.push(block.hex());
  return {
    record: {
      build: build.hex(), buildTerrain: builtTerrain, frames: f, blocks,
      end: { finished: !!game.gameFinished, unplayable: !!game.stateIsUnplayable, out: game.lemmingsOut, saved: game.lemmingsIn, removed: game.lemmingsRemoved },
      nonInteger: nonInteger || undefined,
    },
    schema, frames,
  };
}

function solutionsList(prefix) {
  const index = JSON.parse(fs.readFileSync(path.join(WEB, "solutions", "index.json"), "utf8"));
  const entries = Object.entries(index.levels).map(([id, v]) => ({ id, ...v }));
  return entries.filter((e) => e.status === "solved" && (!prefix || e.id.startsWith(prefix)));
}

async function main() {
  const args = process.argv.slice(2);
  const mode = args[0];
  const opt = (name) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : undefined; };
  const prefix = args.slice(1).find((a, i, all) => !a.startsWith("--") && !(all[i - 1] || "").startsWith("--"));
  const env = await setup();
  const t0 = Date.now();

  if (mode === "trace") {
    const entry = listLevels(ASSETS).find((l) => l.id === prefix) || listLevels(ASSETS, prefix)[0];
    const { level, game } = await buildGame(env, entry);
    const nxrp = opt("--nxrp");
    const r = run(game, level, { replay: nxrp ? Lemmix.Replay.parse(fs.readFileSync(nxrp, "utf8")) : null,
      maxFrames: +(opt("--frames") || 3000), nukeWhenOutOfTime: !!nxrp, keepFrames: true });
    process.stdout.write(r.frames.map((h, i) => (i + 1) + " " + h).join("\n") + "\n");
    return;
  }

  let items;
  if (mode === "noinput") items = listLevels(ASSETS, prefix).map((l) => ({ entry: l }));
  else if (mode === "solutions") {
    const all = listLevels(ASSETS);
    items = solutionsList(prefix).map((s) => ({ entry: all.find((l) => l.id === s.id), solution: s })).filter((x) => x.entry);
  } else { console.error("usage: sim.js noinput|solutions|trace [prefix]"); process.exit(2); }

  const maxFrames = +(opt("--frames") || (mode === "noinput" ? 3000 : SOLUTION_MAX_FRAMES));
  const out = { webSha: require("child_process").execSync("git rev-parse --short HEAD", { cwd: WEB }).toString().trim(),
    mode, block: BLOCK, terrainEvery: TERRAIN_EVERY, maxFrames, schema: null, levels: [] };
  let errors = 0, nonInt = 0;
  for (const { entry, solution } of items) {
    const row = { id: entry.id, url: entry.url };
    try {
      const { level, game } = await buildGame(env, entry);
      let replay = null;
      if (solution) {
        const file = path.join(WEB, "solutions", solution.file || entry.id.replace(/\.nxlv$/, "") + ".nxrp");
        row.nxrp = path.relative(WEB, file);
        replay = Lemmix.Replay.parse(fs.readFileSync(file, "utf8"));
      }
      const r = run(game, level, { replay, maxFrames, nukeWhenOutOfTime: !!solution });
      out.schema = out.schema || r.schema;
      Object.assign(row, r.record);
      if (row.nonInteger) nonInt++;
    } catch (e) {
      errors++;
      row.error = String(e && e.message || e);
    }
    out.levels.push(row);
    if (out.levels.length % 50 === 0) process.stderr.write(out.levels.length + "/" + items.length + " " + ((Date.now() - t0) / 1000).toFixed(0) + "s\n");
  }
  fs.mkdirSync(path.join(OUT, "sim"), { recursive: true });
  const file = path.join(OUT, "sim", mode + (prefix ? "-" + prefix.replace(/[^A-Za-z0-9]+/g, "_") : "") + ".json");
  fs.writeFileSync(file, JSON.stringify(out, null, 0).replace(/\},\{"id"/g, '},\n{"id"') + "\n");
  console.log(`${items.length} runs, ${errors} errors, ${nonInt} with non-integer numbers, ${((Date.now() - t0) / 1000).toFixed(1)} s -> ${path.relative(process.cwd(), file)}`);
}

main().catch((e) => { console.error(e); process.exit(1); });
