# PixelStacking – manual for AI agents (read this completely before doing anything)

You are about to drive **PixelStacking**, a voxel / sprite-stacking model and animation editor, from the command line.
Everything the GUI can do with models is available through one console program, **`pstk.exe`**, which sits in the same folder
as `PixelStacking.exe`. It never opens a window. The results are ordinary `.pstk` files the human can open in the GUI
(Ctrl+O) at any time, and PNG / GIF files for games.

> **Rule 1 – read first.** If you have not read this whole manual in this session, stop and read it (`pstk manual` prints the same text).
> **Rule 2 – never edit a `.pstk` by hand.** It is a binary file. Only change it with `pstk` commands.
> **Rule 3 – look at your work.** After every meaningful step run `pstk render` (then look at the PNG) or `pstk slice` / `pstk info`. Do not assume.

## 1. How to run

```
pstk <command> [arguments] [--options]
pstk help                 # all commands with one-line descriptions
pstk help <command>       # one command
pstk manual               # this text
```

* Windows: `pstk.exe` (PowerShell: `& "C:\path\to\pstk.exe" info model.pstk`). Output is UTF-8 text on stdout, errors on stderr
  as `error: ...`. Exit code 0 = success, 1 = the operation failed, 2 = wrong usage (the usage line is printed).
* A command that modifies a file **saves it in place**. Add `--out other.pstk` to leave the original untouched. If a command fails
  nothing is saved (the file is unchanged).
* Run commands on one file **one after another**, never in parallel. If the human has the same file open in the GUI, tell them to
  re-open it afterwards (the GUI does not auto-reload; saving in the GUI later would overwrite your work).
* Unknown `--options` are an error (so typos are caught). Options can be written `--name value` or `--name=value`.
* **PowerShell quoting:** always put hex colours in quotes (`"#3a5fcd"`; an unquoted `#` starts a comment), or write them without the `#` (`3a5fcd`).
  Comma lists such as `--move 5,0,0` / `--body 0,0,1` work quoted or unquoted. Paths with spaces need quotes.

## 2. Concepts you must know

**Coordinates.** A model is `W × H × D` voxels (each 1..128): **x = length (0..W-1), y = width (0..H-1), z = height (0..D-1, z is up)**.
Everything is **0-based**: coordinates, frame numbers, layer numbers, slice numbers. (The GUI shows 1-based numbers; the CLI never does.)
A character normally faces **+y**.

**Slices and views.** The GUI edits one flat *slice* at a time seen from one of six views: `top, bottom, front, back, left, right`.
`pstk slice <file> <view> <index>` prints one slice as text and tells you how the canvas maps to x/y/z
(`right=+X down=+Y toward-viewer=+Z`...). Use it to learn the mapping instead of guessing.
For the **top** view the slice index is the height z, for **front/back** it is y, for **left/right** it is x.

**Colours.** Colours go into a shared palette of at most **255** entries (index 0 = transparent). You never handle indices:
pass a colour and `pstk` adds it. Accepted colour syntax: `#RRGGBB`, `#RGB`, `#RRGGBBAA` (AA = opacity), `R,G,B[,A]` (0..255),
names (`red green blue yellow orange purple pink brown gray black white cyan magenta skin ...`), and `erase` (clears the voxel).
Semi-transparent colours are allowed.

**Frames.** One file = an animation of 1..n frames of identical size (`fps` 1..30). A new file has 1 frame.

**Layers.** Optional. Layer 0 is the bottom; upper layers cover lower ones; hidden layers are not rendered or exported.
Each layer has its own content in every frame. A file starts with one layer ("single layer": the frame itself is the layer).
Commands that draw take **`--frame N|all`** (default 0) and **`--layer N`** (default 0). Layers exist to move parts around
independently and merge them later (components, objects).

**Rig (parts & joints).** For animating by posing: a *bone* has a name, a *pivot* (joint position, voxel coordinates, may be fractional),
a rotation *axis* (X, Y or Z) and an optional *parent* bone. Voxels are *marked* per frame as belonging to a bone; unmarked voxels are the body.
Rotating a bone swings its voxels around the pivot, children follow the parent. The rig is stored once per file; marks are stored per frame
(on the composite, not on layers – so **flatten layers before building a rig**: `layer-flatten`).

## 3. Command overview

| Area | Commands |
|---|---|
| Create / inspect | `new` `info` `palette` `slice` `render` |
| Draw voxels | `set` `voxels` `box` `sphere` `line` `clear` `palette-set` |
| Images | `import-image` `inflate` |
| Frames | `frame-add` `frame-remove` `frame-move` `fps` |
| Layers | `layer-add` `layer-remove` `layer-rename` `layer-move` `layer-dup` `layer-merge` `layer-flatten` `layer-visible` `layer-import` `layer-shift` |
| 3D magic wand | `wand` |
| Rig / animation | `rig-add` `rig-remove` `rig-mark` `bake` `presets` `preset` |
| Export | `export-png` `export-gif` `export-strip` |

Run `pstk help` for the exact usage lines. The important ones in detail:

* `new <file> W H D [--fps N] [--force]` – creates an empty model. Refuses to overwrite without `--force`.
* `info <file>` – size, frames, fps, layers (name, visible, voxel counts), palette use, per-frame voxel count and **bounding box**, rig.
  Run it first on any file you did not create.
* `set <file> x y z <colour>`, `box <file> x0 y0 z0 x1 y1 z1 <colour> [--hollow]`, `sphere <file> cx cy cz r <colour> [--hollow]`,
  `line <file> x0 y0 z0 x1 y1 z1 <colour>`, `clear <file>`. Shapes are clipped to the model. Colour `erase` carves.
* `voxels <file> <list.txt|->` – bulk: one `x y z colour` per line. **The fastest way to build something detailed**: generate the list with a
  script and feed it in (lines starting with `//` are comments; `-` reads stdin).
* `import-image <file> <image> --view front --slice 0 [--fit contain|center|topleft|stretch] [--strip-width N]` – paints a PNG/JPG/GIF/BMP onto
  a slice. With `--strip-width N` the picture is a sprite-stacking strip: tile k (N px wide) goes to slice+k. Transparent pixels keep what
  is already there unless `--overwrite-transparent`.
* `inflate <file> <image> [--view front] [--profile round|smooth|cone|flat] [--side both|front|back] [--depth auto|N] [--min-depth 2] [--threshold 128] [--keep]`
  – **2D picture → 3D**: the alpha channel is the silhouette; every pixel becomes a column whose length grows with its distance from the outline
  (`round` = balloon-like, `flat` = plain extrusion). `--depth N` caps the thickness, `--side front` makes a relief with a flat back.
  The picture is fitted to the model's canvas for that view (`--fit contain` keeps its aspect ratio), so create the model with a size
  that suits the picture first (e.g. 32×16×32 for a front-view picture 32 px wide, 32 px tall; thickness along y).
* `layer-*` – see §5.2. `wand` – see §5.3.
* `rig-add <file> <name> px py pz [--axis X|Y|Z] [--parent bone]`, `rig-mark <file> <bone> box ...|wand x y z|clear`, `preset`, `bake` – see §5.4.
* `render <file> out.png [--frame N] [--layer N] [--yaw 35] [--elev 30] [--scale 6] [--bg #RRGGBB]` – one frame as a picture.
  yaw turns the model around the vertical axis (degrees), elev is the camera pitch (90 = looking straight down, 0 = from the side).
  **Look at the result**, and render from a second angle (e.g. `--yaw 215`) to check the back.
* `export-png` (sprite sheet: rows = directions around a full turn, columns = frames, + `.json`), `export-gif`, `export-strip`
  (sprite-stacking strip for engines that stack slices themselves).
* **Outlines** (`render`, `export-png`, `export-gif`): add `--outline` to draw dark lines that make edges readable: an outer ring around
  the model plus edge lines where faces of different orientation meet or where something in front overlaps something behind. The line
  colour is the base colour darkened. Options: `--no-silhouette` / `--no-creases` (draw only one kind), `--outline-width 1|2|3` (pixels of
  the rendered picture; use 2–3 for large `--scale`), `--outline-tone light|medium|dark`. The outer ring needs room, so the picture is a
  little larger than without `--outline`.

## 4. Typical recipes

### 4.1 Build a model from primitives / a script
```
pstk new knight.pstk 24 16 40
pstk box knight.pstk 8 5 0 15 10 17 "#3a5fcd"            # legs+torso block
pstk sphere knight.pstk 11 7 24 5 skin                    # head
pstk voxels knight.pstk details.txt                       # many single voxels from a script
pstk render knight.pstk knight.png --yaw 35 --elev 30
```
Plan the proportions in voxels first (a 40-high character: legs 0..15, torso 16..29, head 30..39). Keep the character centred on x/y so mirror
parts (left/right) are symmetric: with W=24 the centre is between x=11 and x=12.

### 4.2 2D picture → 3D
```
pstk new coin.pstk 32 16 32 --force
pstk inflate coin.pstk coin.png --view front --profile round --depth auto
pstk render coin.pstk coin3d.png --yaw 30 --elev 20
```

### 4.3 Assemble components (objects) into one model
```
pstk layer-import body.pstk hat.pstk --name hat          # hat becomes a new top layer (centred if sizes differ)
pstk layer-shift  body.pstk 1 0 0 12                      # move layer 1 up by 12 (+z), frame 0; add --frame all for every frame
pstk render body.pstk check.png                           # verify placement
pstk layer-merge  body.pstk 1                             # merge layer 1 down into layer 0
```
Imported components keep their own layer, so a wrong placement is undone by `layer-remove` and trying again (or use `--out` to keep the original).

### 4.4 Cut a part out of a finished model and move it
`wand` selects the block that is **3D-connected** to a voxel. `--to-layer` cuts it into a new layer so it can be moved independently:
```
pstk wand model.pstk 10 8 20 --same-colour                        # just report size and bounds
pstk wand model.pstk 10 8 20 --to-layer --layer-name sword --move 4,0,0
```
`--move dx,dy,dz` is in voxel axes (not screen directions). If the block would leave the model the command fails and changes nothing.
Parts touching other parts of the same colour need `--same-colour` off/on and sometimes `--diagonal`; check the reported bounds.

### 4.5 Animate with a rig and a preset
```
pstk layer-flatten char.pstk                                       # one layer only
pstk rig-add char.pstk left_thigh  11 8 15                         # pivot = hip joint, axis X by default
pstk rig-add char.pstk right_thigh 13 8 15
pstk rig-add char.pstk left_shin   11 8 8 --parent left_thigh      # knee
pstk rig-mark char.pstk left_thigh box 10 6 9 11 10 15             # mark the voxels of that leg segment
pstk rig-mark char.pstk left_shin  box 10 6 0 11 10 8
...                                                                # (same for the right side, arms, head ...)
pstk presets                                                       # list presets and roles
pstk preset char.pstk walk --frames 8                              # appends 8 frames
pstk export-gif char.pstk walk.gif --yaw 35 --elev 25 --bg "#222222"
```
Rules that make presets work:
* The character faces **+y** and bones use **axis X** (a swing forward/back). If the swing looks backwards use `--reverse-all` or `--reverse role,role`.
* Bone names are matched to roles by words: left/right (`左 右 left right`, or a lone `l`/`r` like `leg_L`) + `thigh/leg 大腿`, `shin/calf 小腿`,
  `arm/上臂`, `forearm/前臂`, `head 頭`, `torso 軀幹`, `wing 翅`, `tail1..3 尾`. Chibi / secondary parts are recognised too: `skirt 裙`, `hair 前髮/後髮 (hairF, hairB)`, `髮飾 accessory`, `ahoge 呆毛`, `cape 披風`, `ear 耳` (left/right). The built-in walk, run, idle, jump, bow, nod and attack presets make these follow the body with a delay; a model without them is unaffected. Otherwise assign explicitly: `--assign legL=hip_l,legR=hip_r`.
* A preset needs marked voxels. If every generated frame equals the source the command fails and says so.
* Parts must overlap 1–2 voxels at joints, otherwise gaps show when they swing.
* The preset library always poses the **source frame** (`--source-frame`, default 0), so keep frame 0 as a clean standing pose.

### 4.6 Export for a game
```
pstk export-png char.pstk out/char.png --dirs 8 --scale 4        # 8 directions x all frames sprite sheet + char.json
pstk export-strip char.pstk out/char_strip.png                    # engines that rebuild the stack from slices
```

## 5. Reference notes

### 5.1 Targets and limits
* Size ≤ 128 per axis, palette 255 colours (an error tells you when it is full), ≤ 64 layers, fps 1..30, preset ≤ 64 frames.
* Voxels written outside the model are skipped (bulk commands report the count); a shape completely outside is an error.
* `--frame all` is supported by drawing commands, `layer-shift`, `rig-mark`; `wand` and `bake` work on one frame.

### 5.2 Layers
`layer-add [name] [--at I]`, `layer-remove i`, `layer-rename i name`, `layer-move from to`, `layer-dup i [name]`, `layer-merge i` (into i-1),
`layer-flatten` (all visible layers into one; hidden are dropped), `layer-visible i on|off`, `layer-import other.pstk`, `layer-shift layer dx dy dz [--frame all]`.
Drawing into a layer: `pstk box model.pstk ... --layer 2`. A layer added to a file with several frames is empty in every frame.

### 5.3 3D magic wand
`wand <file> x y z` needs a **coloured** voxel at the seed (in the chosen frame/layer). Default connectivity: faces only (6 neighbours);
`--diagonal` adds edges and corners (26). `--same-colour` limits the block to the seed's colour.
`--delete` erases the block. `--move` and `--to-layer` can be combined.

### 5.4 Rig details
* `rig-mark <file> <bone> box x0 y0 z0 x1 y1 z1` marks every coloured voxel in the box; `... wand x y z` marks a whole connected block
  (handy: put a gap or a different structure between parts, then mark each by wand); `... clear` removes that bone's marks.
  Marks are per frame – `--frame all` for frames that are copies of each other. Re-mark if you change the shape.
* `bake <file> [--angle bone=deg]... [--offset bone=dx,dy,dz]... [--body dx,dy,dz]` appends one posed frame (rotation of a bone is around its axis
  through its pivot; positive angle sign follows the right-hand rule around the axis).
* `preset <file> <name|number> [--frames N] [--easing linear|in|out|inout|back|bounce] [--amp 1.2] [--reverse roles] [--reverse-all] [--assign role=bone,...] [--loop|--no-loop] [--source-frame N]`.
  `--amp` scales all swings (1.5 = more exaggerated). Easing changes the pacing (slashes: `in`, landings: `bounce`).
  Loop presets (walk, run, idle...) are generated so the last frame leads back into the first one.

### 5.5 Reading `info`
`bounds` per frame are the min/max voxel coordinates that contain anything: use them to centre, to see if something is cut off at the
model edge (bounds touching 0 or size-1), and to verify that a move did what you expected.

## 6. Checklist before you tell the human you are done
1. `pstk info file.pstk` shows the expected size, frames, layers and a sensible bounding box.
2. You rendered at least two angles (`--yaw 35` and `--yaw 215`) and looked at them; for animations you looked at several frames
   (`render --frame N`) or exported a GIF.
3. Nothing is cut off at the model edges, no stray voxels, symmetric parts are symmetric.
4. You told the human the output file names and that they can open the `.pstk` with PixelStacking.exe (File → Open), including any
   commands you ran, so they can reproduce or tweak them.
