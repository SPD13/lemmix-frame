# Lemmix.Core

The port of the web version's Lemmix engine (`web/lemmix/js/*`) and the pure-data parts of its
3D layer (`web/3d/js/terrain.js`, `depth.js`, `portals.js`, `bridge.js`, `envgen.js`, …), with no
Godot dependency, so it is tested with `dotnet test` on the Mac and in the Frame's runtime image.

## Porting rules

The port is method for method: same names (PascalCase), same order of operations, same data
layout, so a diff against the JS reads line by line and the oracles can point at the first
difference. Every rule below comes from something in the JS that a naive port gets wrong.

Numbers
- `Math.round` → `JsMath.Round` (halves go up). Never `Math.Round` (banker's rounding):
  `pixels.js:113` alpha blending feeds the physics map.
- `x | 0`, `>> 0` → `JsMath.ToInt32`; `>>> 0` → `JsMath.ToUint32` or `uint` arithmetic.
  `int` arithmetic is unchecked (the project turns overflow checks off), as JS int32 ops wrap.
- Doubles stay doubles, in the JS operation order (`level.js:471` `pd[p] * mod >= cutoff`,
  `mod = sol / 255`). Never `float`.
- `Math.floor` / `Math.trunc` / `%` on negatives: `Math.Floor` / `Math.Truncate` / C# `%`
  (same sign rule as JS).
- The seeded xorshift (`level.js:49-58`) uses `uint`.

Collections
- Stable sorts only (`lemgame.js:263` replay entries by frame, `styles.js:244` by zIndex):
  LINQ `OrderBy`, never `List.Sort`.
- Replay entries keep insertion order (`lemgame.js:276-326`): a `List`, not a dictionary by frame.
- `for (const l of this.lemmings)` visits lemmings pushed during the loop
  (`lemgame.js:2252, :2271`): an index loop re-reading `Count`, never `foreach`.
- `Object.keys` copies (`Lemming.assign` / `clone`, `lemgame.js:159-165`): a `CopyFrom`
  covering every field; arrays shared by reference stay shared (`jumpPositions` is replaced,
  never mutated in place, `lemgame.js:1889`).
- `x === undefined` lookups → `TryGetValue`.

Text
- Invariant globalization is on; use `ToUpperInvariant` and ordinal comparisons.
- `\d` in .NET regexes matches every Unicode digit: write `[0-9]`.
- JS `trim()` removes U+FEFF and `fetch().text()` drops a BOM: strip it when reading.
- Sort orders built with `localeCompare(…, {numeric: true})` (the index builders) have an oracle
  of their own: the C# comparer must reproduce the dumped order.

Files
- The Frame's filesystem is case-sensitive and the styles are not consistent (`Objects/` vs
  `objects/`): every read goes through the case-insensitive path map of `IFileSource`.
- PNGs decode as `pngjs` does (the oracle's path), not as a browser canvas would.

Memory
- Rewind states are pooled (≈3.6 MB each on a big level); nothing allocates per frame on the
  hot path that a pool can hold.
