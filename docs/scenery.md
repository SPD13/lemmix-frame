# Sceneries: making a gallery's room to the horizon

How the room around the board is made for a gallery: a ground running out to a hazy horizon,
rings of the gallery's own pieces from near to far, and a sky. The first one was made for
`orig_dirt` on 3 Oct 2026. This document is the context a session needs to make a scenery for
another gallery: what it is, how the generator works, how to judge and tune the result, and the
traps already met. `docs/godot-implementation.md` 6.11 is the short reference.

A **gallery** is a level's theme style (`THEME orig_dirt` in the `.nxlv`), the folder
`neolemmix/styles/<style>/` with its `terrain/` pieces. Every level of a style shares its
gallery's room.

---

## 1. What a scenery is

A scenery is a set of files made offline. They live under the asset root (not in git, like the
installed styles):

```
<assets>/3d/env/<style>/scenery/
  scenery.json            the manifest (core/Lemmix.Core/Render/Scenery.cs: SceneryManifest)
  ground.png              a seamless tile, 512 x 512
  layer-1-rubble.png      one strip per ring, 2048 wide, near to far
  layer-2-pillars.png
  layer-3-overhang.png
  layer-4-outcrops.png
  layer-5-ridge.png
  layer-6-far.png
  layer-7-horizon.png
  preview.png             the generator's view from the player's place (not read by the app)
```

On the Mac the asset root is `../LemmingsJS` (`WEB_ASSETS`). On the Frame it is
`~/.local/share/godot/app_userdata/Lemmix/assets/`.

When the room is in mode `full` and the level's gallery has a `scenery.json`,
`EnvironmentView` builds none of envgen's ring pictures. `SceneryView` shows the scenery
instead:

| Part | Geometry | Picture |
|---|---|---|
| Sky | a sphere, 120 m, round the eye | none: a gradient by elevation (zenith, high, horizon, below) |
| Ground | a disc at the floor, 115 m | `ground.png` tiled every 1.28 m, trilinear |
| Strips | an open drum per layer round the player's place | `layer-N-*.png`, alpha cut-out |

The room's modes (settings toggle, `--environment=`) are:
- `none`: no room.
- `fog`: envgen's haze alone, as galleries looked before sceneries.
- `full`: the scenery when there is one, else envgen's rings.

`--scenery=off` forces the rings even when a scenery exists.

### The look, in one shader

`SceneryView.ShaderCode` paints everything, unshaded:

1. **Grade.** The texel is pulled toward grey by the layer's `desat`, then multiplied by its
   `grade` (brightness).
2. **Haze.** The graded colour is mixed toward the sky's colour in the direction it is seen,
   by `SceneryLook.Haze`:
   - `k = min(max, 1 - e^(-d / distance_m))`;
   - plus a mist near the ground that grows with distance:
     `mist * (1 - smoothstep(0, mist_m * (0.4 + d/30), height)) * smoothstep(3, 30, d)`.
   Because the haze colour is the sky behind the thing, far strips melt into the horizon with
   no seam.
3. **Fade.** A strip with `fade_top` melts into the sky over that fraction of its height from
   the top. The overhang uses it.
4. **Dither.** An ordered dither of about one step of the 10-bit buffer breaks the dark
   gradients' rings.

Alpha test only, no blending, so nothing is sorted. The C# twin of the colour maths is
`SceneryLook` (`Sky`, `Haze`, `Grade`): the generator's preview uses it, and so do the tests.
**If you change the formula, change both.**

### Two rules behind the design

**One angular pixel size.** Every strip is 2048 texels round, whatever its radius. So a texel
spans the same angle near or far: 3.07 mrad, about one board pixel seen from 0.9 m. The pixel
art keeps one size through the whole depth, like the parallax layers of a 2D game, and matches
the board. Consequences:
- A strip's texel is `2π r / 2048` metres, so its row count is `height_m / texel`.
- The pieces are stamped 1:1. Never scale them per layer.
- A far mountain is "made of few big pieces"; a near pillar of many small ones.

**The board comes first.** The direction straight ahead is u = 0.5 on every strip (the board's
side, -z in the room's frame), and the strips are kept **calm** there:
- nothing near;
- the far ridges low and hazy;
- the formations and the overhang on the sides and behind.

Near layers are dark and desaturated (low-key framing); far layers get lighter as they sink
into the haze. The board, bright and saturated on its dark slab, should be the most contrasted
thing in view.

---

## 2. Making one

```bash
cd lemmix-frame/tools/scenery-gen
WEB_ASSETS=../../../LemmingsJS dotnet run -c Release -- gen <style>        # ~4 s
#   --out <dir>   write somewhere else (e.g. a scratch dir while iterating)
WEB_ASSETS=../../../LemmingsJS dotnet run -c Release -- sheet <style> /tmp/sheet.png   # contact sheet of the pieces
```

The run prints how it sorted the pieces. **Read that first**: most problems of a new gallery
come from pieces in the wrong role.

### 2.1 Pieces and their roles (`tools/scenery-gen/Pieces.cs`)

Each terrain piece is cropped to its box, and its alpha is made binary. Then it is measured:
size, fill (opaque / box), aspect (h / w), mean colour, green or not, and saturation.

| Role | Rule (in order) | Used for |
|---|---|---|
| left out | steel; name matches `bridge|sign|chain|rope|ladder|arrow|brick|plank`; saturation < 0.4 × the style's median (grey things: bones, metal); aspect < 0.3 (flat logs, planks) | nothing |
| tuft | green and ≤ 40 px, aspect ≤ 1.4 | grass on tops; moss under overhangs |
| hang | green and ≤ 40 px with aspect > 1.4, or fill < 0.42, aspect > 0.9, ≤ 48 px | roots, creepers from undersides |
| (open) | fill < 0.42, anything else | nothing |
| spire | aspect ≥ 1.45 and h ≥ 40 | stacked into pillars; hung as stalactites |
| mass | box area ≥ 900 | packed into ridges, mountains, footings |
| rubble | box area ≥ 60 | the near rubble ring; the ground |

The crevice colour (the gaps inside masses, the ground's background) is the darker quarter of
the solid pieces' shadows.

**When the rules leave too little** (added when making all the device's galleries):
- **Relaxing.** The left-out rules are relaxed one step at a time until the style has at least
  8 solid pieces (mass + spire + rubble): first made pieces are kept, then grey ones, then
  steel. A brick wall style (`sqron_turrican2wall`) keeps its bricks; a metal style its steel.
- **Green.** Green counts as greenery (tuft / hang) only when green pieces are fewer than half
  the style. Otherwise green is the material (`l3_biolab`, `l2_outdoor`, `flopsy_starlight`…).
- **Promotion.** A style of small tiles has no piece of 900 px² or more. Its biggest solid
  pieces are promoted to masses until there are 4–6 (they stay rubble too), so the ridges have
  something to pack (`namida_space`, `namida_lab`, `ohno_brick`, `dex_lr2_industry`…).
- **Nothing at all.** If no solid piece is left, `gen` exits 3 and writes nothing; the gallery
  keeps the rings.

The printout ends with `note` lines saying which of these applied.

For orig_dirt:
- **mass:** clump_01/04/05/07/08/09, rocks_01.
- **spire:** clump_03, clump_06.
- **tuft:** grass_01–04, moss_01.
- **hang:** moss_02–06, roots_02–04.
- **rubble:** clump_02/10, rocks_02–05.
- **left out:** bridges, signs, steel, bones, circles (grey), tree (flat).

**For a new gallery**, check that:
- `mass` holds the big natural lumps (not buildings or props);
- `spire` holds tall solid pieces;
- greenery or the style's equivalent lands in `tuft` / `hang`.

If the rules misplace pieces, prefer a general rule. When no general rule works, add a small
per-style override (a name list) in `PieceSet.Load`. Keep it data, not code paths. Styles with
no green (snow, metal, crystal) will have empty tuft/hang lists. That is fine: those passes do
nothing.

### 2.2 The layers (`Layers.Recipe`)

Seven strips, near to far. The fields are those of `LayerSpec`:

| name | kind | radius m | strip height m | skyline lo–hi m | calm inner/outer/floor | spires (lo–hi m) | tuft every | hang p | grade / desat |
|---|---|---|---|---|---|---|---|---|---|
| rubble | ridge (Small) | 3.8 | 0.7 | -0.15–0.5 | 0.10 / 0.22 / 0 | – | 70 | – | 0.50 / 0.35 |
| pillars | ridge | 6.5 | 5.2 | -0.2–0.8 | 0.12 / 0.24 / 0 | 9 (1.8–4.6) | 55 | 0.22 | 0.42 / 0.40 |
| overhang | hang, bottom 5 m, fade_top 0.45 | 9.5 | 8.0 | 1.2–4.8 (depth from top) | 0.08 / 0.30 / 0.3 | 14 (1.2–3.6) stalactites | 40 | 0.35 | 0.38 / 0.40 |
| outcrops | ridge | 14 | 8 | 0.6–5.0 | 0.05 / 0.18 / 0.35 | 6 (3–7) | 45 | – | 0.50 / 0.45 |
| ridge | ridge | 25 | 11 | 1.5–8.5 | 0.04 / 0.16 / 0.45 | 4 (6–10.5) | 40 | – | 0.60 / 0.45 |
| far | ridge | 44 | 16 | 3–13 | 0.03 / 0.14 / 0.55 | – | – | – | 0.70 / 0.50 |
| horizon | ridge | 78 | 16 | 2–11 | 0 / 0.10 / 0.70 | – | – | – | 0.80 / 0.50 |

What the fields mean:
- **Skyline.** `lo + (hi - lo) * (0.5 + 0.5 * wobble(u))`, multiplied by `calm(u)`. The wobble
  is a sum of whole-number-frequency sines (`FMin..FMax`, roughness `Rough`), so the strip
  closes on itself.
- **Calm.** `calm(u) = floor + (1 - floor) * smoothstep(inner, outer, |u - 0.5|)`. `floor` 0
  means nothing straight ahead.
- **Strip height.** It only has to hold the tallest thing. The actual metres are written to the
  manifest from the rows.

How a strip is drawn (`Layers.Build`):
1. **Spires** at random angles outside the calm zone, at least 0.025 apart. Each is a footing of
   2–4 masses, then a shaft of spire pieces (and tall masses) stacked with overlap. The shaft
   leans a little; 35 % end in a wide cap.
2. **Ridge.** Masses are packed in rows from the ground up. A piece is kept only where the
   skyline is at least 0.35 of its height (no half-buried band), and if it doesn't stand far
   over the skyline. Later rows go *behind* earlier ones (stamps never overwrite).
3. **Crevices.** Empty pixels under a column's top with rock on both sides within 20 px are
   filled with the crevice colour. Big layers only.
4. **Shade.** Darker with depth under each column's top (down to 0.55), and darker in the
   bottom rows.
5. **Tufts** on column tops; **hangs** under undersides.
6. **`hang` kind.** The whole strip is turned upside down, roots and moss are hung from its
   lower edge, and its top is darkened a little.

**Ground** (`Layers.Ground`): masses and rubble packed with wrap on both axes; holes in the
crevice colour; 72 % pulled to the mean colour (quiet); a few tufts.

**Colours** (`Program.cs`), from envgen's palette of the whole style (`EnvGen.DerivePalette`):

| Colour | Value |
|---|---|
| horizon | `mix(palette.fog, brightest material, 0.36)` |
| high | `mix(horizon, palette.bg, 0.62)` |
| zenith | `palette.bg × 0.45` |
| below | `horizon × 0.8` |

Fog defaults: `distance_m` 24, `max` 0.94, `mist` 0.5, `mist_m` 2.5.

**Kept as calm as orig_dirt** (`tools/scenery-gen/Look.cs`). orig_dirt is the reference, and
the caps sit just above its own levels, so it is unchanged:

| What | Cap |
|---|---|
| horizon | luma ≤ 85; chroma (max - min channel) ≤ 112, pulled toward its grey |
| high | luma ≤ 50, same chroma cap |
| zenith | luma ≤ 30, same chroma cap |
| each strip's grade | lowered so its mean luma before grading counts as at most 62 |
| ground's grade | lowered so its mean luma before grading counts as at most 75 |

Without the caps, bright or vivid styles glared (namida_purple, namida_honeycomb, l2_egyptian,
namida_desert), and white ones (marble, bubble, snow, clouds) were far brighter than the board
allows. The run prints each strip's mean luma.

The recipe is the same for every style today. A gallery with a different character (an open
sky style, a city, a crystal cave) may want its own recipe. In that case:
- make `Recipe` selectable (a `--recipe <file.json>` read into `LayerSpec`s, or a per-style
  table);
- keep the radii, the 2048-round rule and the calm-ahead rule;
- keep orig_dirt's output unchanged.

For a style with a real sky (outdoor, `lemmings` tilesets, `l2_outdoor`):
- drop or shrink the overhang;
- lighten the zenith;
- consider a band of the style's wallpaper on the sky.

### 2.3 Tuning without regenerating

Everything in `scenery.json` except the pictures is read at run time:
- `fog.*`;
- `sky.*` colours;
- per layer: `grade`, `desat`, `fade_top`, `radius_m`, `bottom_m`;
- `ground.grade` / `ground.desat` / `ground.tile_m`.

Edit the file in the asset root and render again. Then **port what you settled on back into
the generator** (Recipe / Program.cs), so that a rerun doesn't undo it.

---

## 3. Judging it

### 3.1 The generator's preview

`preview.png` shows straight ahead ±108°, from -35° to +55° of elevation, one texel per angular
texel. It uses the same sky, haze and grades as the app (`Preview.cs`, `SceneryLook`). It is
fast and good for composition. It is about 2.5× smaller than in the headset.

For many galleries at once, `tools/scenery-all.sh [pack...]` makes the scenery of every
gallery the installed levels use. With `REVIEW=<dir>` it also writes review sheets: 8 previews
a sheet at half size, two a row, in the order of `sheets.txt` (`scenery-gen montage` makes
them).

### 3.2 The headset view in the render box

```bash
tools/scenery-views.sh <style> '<level id of that style>' build/scenery-shots/<style> [full|fog|none]
```

It renders the `vr-scene` shot (one eye, 1280×960, 75° vertical) looking ahead, left 70°,
behind and up. It takes about 40 s per view. `SKIP_GEN=1` renders without regenerating.

The shot also takes these variables:

| Variable | What it does |
|---|---|
| `SHOT_LEVEL` | the level |
| `SHOT_ENV` | the room's mode |
| `SHOT_LOOK="yaw,pitch"` | degrees, left and up positive; turns the head after the board is placed |
| `RENDER_EXTRA="3d/env/<style>/scenery"` | copies the scenery into the box |

`SHOT_DEBUG=1` prints every visible mesh to `godot.log` (`[dump] …/environment/scenery/…`).

Pick a level of the gallery: `grep -l "THEME <style>" levels/**/*.nxlv`.

Look for:
- **The board pops.** The scenery is darker, greyer and hazier than the board; nothing busy
  stands right behind it.
- **Depth.** Clear steps from dark near silhouettes to pale far ridges. The ground meets the
  haze with no edge. The horizon glows a little.
- **Style.** You can tell which gallery it is from the pieces. Nothing reads as a foreign
  object (grey bones, planks, signs).
- **No artifacts.** See section 4.

To read exact colours from a shot (no PIL on this Mac), decode the PNG in pure Python (zlib +
filters) and print pixels at chosen coordinates. A ~40-line script did it for orig_dirt.

### 3.3 Tests

- `core/Lemmix.Core.Tests/Render/SceneryTests.cs`: the manifest, the scaling, the haze and sky
  maths.
- `app/Test/BoardTests` covers the modes.
- `make app-test` must stay at 0 failures.

Known failures on the Mac **not** caused by sceneries:
- `EnvGenTests.TheRoomIsDrawnAsTheWebDrawsIt` (Just dig!, orig_dirt) fails on the base commit
  too.
- In a fresh worktree, the Markers / Sim / Terrain oracle tests fail because `web/` is empty.

---

## 4. Traps already met (and their fixes)

| Symptom | Cause | Fix (in place) |
|---|---|---|
| Grey chains and bones in the rocks and roots | grey decor pieces sorted as mass / hang | saturation rule in `Pieces.cs` |
| Orange balls and planks in the rubble | bridges, circles, flat pieces | name rule, grey rule, aspect < 0.3 |
| Hard vertical cliffs under leaning rocks | crevice fill filled every column down to the ground | fill only gaps with rock on both sides |
| A thin band of rock tops all round at ground level, even straight ahead | pieces kept where the skyline is 0, 40 % of them showing | skip where skyline < 0.35 × piece height |
| Concentric rings on the ground and in the sky | 10-bit linear buffer (Mobile): few steps in dark gradients | ordered dither in linear space, ±1.5/1023 |
| An arc on the ground where the texture changes | nearest-mipmap filtering switches mip levels abruptly at grazing angles | ground sampled trilinear + anisotropic (`ground_map`) |
| Straight top edge of the overhang against the sky | the strip ends | `fade_top` melts it into the sky colour |
| Grass blades look huge near | tufts are 1:1 like everything; stereo makes near size readable | sparse tufts on near layers (`TuftEvery` 55–70) |
| Uniform stick-like pillars | one tall piece stacked | shafts mixing spire and tall masses, lean, caps |
| Everything looks too big in the shot vs the preview | the preview is ~2.5× smaller | judge size in the render box |
| `sqron_turrican2wall` crashed with no pieces | every piece was a "brick" or steel | relaxing the left-out rules (2.1) |
| A green style had only "tufts" and empty ridges | green read as grass | green is greenery only in a minority |
| Tile styles had empty or one-piece ridges | no piece reached mass size | promotion of the biggest blocks |
| Lime, magenta or yellow glare at the horizon; white styles too bright | palette taken as is | the calm caps (2.2) |
| `SHOT_LOOK` did nothing | hooked `app.Ready` after the app was already ready | hook `root.GetTree().ProcessFrame` |

More things to know:
- A wide level makes the room's first ring wider. The strips are then pushed out together,
  round the eye (`SceneryLayout.Scale`: the nearest stays 0.9 m past the first ring), so the
  angles they were drawn for are kept.
- The camera's far plane is 300 m. Keep everything (sky 120 m) well inside.

---

## 5. On the Frame

```bash
tools/export.sh linux-arm64 && tools/frame-deploy.sh play      # the app, when the code changed
tools/frame-scenery.sh <style>                                   # the pictures and manifest
tools/frame-scenery.sh --remove <style>                          # take it off
```

The Frame must have the style installed (setup page) and a level of it to open. Things to check
in the headset that the render box can't show:
- the frame rate when looking toward the horizon, where several strips overlap;
- whether the dither is invisible in stereo;
- whether the near rings' distance and size feel right;
- comfort.

`tools/frame-capture.sh` takes a picture of the headset's view.

## 6. Reverting

| To | Do |
|---|---|
| Undo a gallery's scenery | delete `3d/env/<style>/scenery/` (and `tools/frame-scenery.sh --remove <style>`) |
| Undo all sceneries for a run | `--scenery=off` |
| Undo the feature | the commits on branch `scenery` |

Committed:
- the generator (`tools/scenery-gen/`);
- `SceneryView`, `Scenery.cs`;
- the hooks in `EnvironmentView`;
- the fog mode;
- the scripts.

The pictures are never committed.

## 7. The galleries made so far

On 3 Oct 2026, every gallery the device's levels use (Lemmings Redux, LemmingsPlus All,
NeoLemmix Introduction Pack) got a scenery: 69 galleries, about 82 MB with previews. The one
used gallery not made is `xmas` (12 levels), whose style is not installed. orig_dirt was tuned
by hand; the other 68 come from the general rules plus the caps. Each was judged on its
preview in review sheets, and two (orig_marble, namida_space) in the render box. The ones most
worth a closer look in the headset:
- **Vivid by nature:** namida_purple, namida_psychedelic, namida_candy, l2_circus.
- **Blocky tile styles:** namida_space, l2_shadow, gronkling_minimal.
- **Most played:** orig_marble, orig_pillar, orig_fire, ohno_brick, orig_crystal.

## 8. Checklist for a new gallery

1. Find the style and a level of it. Make the contact sheet (`sheet`) and look at the pieces.
2. `gen <style> --out <scratch>`. Read the sorting printout and fix misplaced pieces.
3. Look at `preview.png`: composition, the calm ahead, depth steps, colours.
4. Generate into the asset root. Run `tools/scenery-views.sh` and judge the four views
   (section 3.2).
5. Tune `scenery.json`, then move the settled values into the generator. Only if the gallery
   needs it, add a recipe for it without changing orig_dirt's.
6. Run `make app-test`, then `tools/frame-scenery.sh <style>` and check in the headset.
7. Record what you learned in section 4.
