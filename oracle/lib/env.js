"use strict";
/** Where the oracle finds the web engine (web/, pinned) and the assets (WEB_ASSETS, a working copy with levels/ and neolemmix/). */
const path = require("path");
const fs = require("fs");
const Module = require("module");

const ROOT = path.resolve(__dirname, "..", "..");
const WEB = path.join(ROOT, "web");
const ASSETS = path.resolve(process.env.WEB_ASSETS || path.join(ROOT, "..", "LemmingsJS"));
const OUT = path.join(ROOT, "oracle", "out");

// web/tools/lemmix-node.js requires pngjs, which lives in oracle/node_modules
process.env.NODE_PATH = [path.join(ROOT, "oracle", "node_modules"), process.env.NODE_PATH].filter(Boolean).join(path.delimiter);
Module._initPaths();

if (!fs.existsSync(path.join(ASSETS, "levels", "index.json")) || !fs.existsSync(path.join(ASSETS, "neolemmix", "gfx")))
  throw new Error("WEB_ASSETS=" + ASSETS + " has no levels/index.json or neolemmix/gfx");

const node = require(path.join(WEB, "tools", "lemmix-node.js"));
module.exports = { ROOT, WEB, ASSETS, OUT, ...node };
