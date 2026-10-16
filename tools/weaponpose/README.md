# weaponpose

Poses one upright weapon picture into all 832 frames of a Crystal player weapon library (`Data\CWeapon\NN.Lib`:
male frames 0-415, female 416-831), using a reference weapon's frames for the line (pommel to tip) in each frame.
Python 3, standard library only. The Scene Lab does the same in the browser (`WebClient/play/weaponPose.ts`, Weapon
tester section: drop the picture on the view).

## How it works

1. Measures the reference weapon (default Wooden Sword, `CWeapon\00.Lib`) in every frame: the principal axis of its
   pixels, and which end is the pommel (the light grey guard sits near the pommel; a colour-profile match and the
   neighbouring frames settle the rest).
2. Reads the picture: weapon pointing straight up (tip at the top), on a transparent background or a flat colour taken
   from the top-left pixel (removed only where it touches the picture's edge, so dark parts inside the weapon stay).
   The axis is the handle's centre line, from the bottom 20 %.
3. For each frame rotates the picture onto the line with the pommel pinned, shortens it with the line
   (foreshortening), and flips it across its axis in the left-facing directions (`--mirror`).

## Commands

```
python -I tools\weaponpose\weaponpose.py render --ref <Client>\Data\CWeapon\00.Lib --input weapon.png --out out --lib 79.Lib
```

| Option | Meaning |
|---|---|
| `--length 1.25` | weapon length relative to the reference (grows past the old tip, pommel fixed); Dragon Slayer (`CWeapon\29`) measures 2.0 |
| `--width 0.8` | thickness relative to the picture's own proportions |
| `--glow 90` | also drop a glow's dark coloured halo on a black background (dark, strongly coloured pixels anywhere); off by default because it eats a dark brown handle too |
| `--mirror none` | no flip; default `DownLeft,Left,UpLeft`; any comma list of `Up, UpRight, Right, DownRight, Down, DownLeft, Left, UpLeft` |
| `--compare` | also writes original-vs-new sheets (`out\compare\`) and accuracy numbers in `report.txt` |

`make-input --ref ... --out upright.png` straightens the reference's longest frame into an upright picture: the input
format, and a round-trip test (render it back with `--compare`; the Wooden Sword gives endpoint error <= 0.9 px and 0
reversed frames).

Writes the `.Lib` (v2, no frame table, like `CWeapon`) and per-action preview sheets. Read only from the client
folder; never write into `Build\` - copy the result into a client's `Data\CWeapon\` yourself.

## Weapons from WoW models (WMVx)

WMVx (`C:\WMVx-0.5.2`, the WoW model viewer fork) renders a weapon on its own with `--item-still`:

```
C:\WMVx-0.5.2\Build2\Release\WMVx.exe --item-still --item 19019 --size 1024 --directions 8 --pitch 0 --out <dir>
```

It writes 8 side views (`d00.png` ... `d07.png`, transparent background). Pick the view that shows the blade flat
(the widest one), crop it, rotate it so the tip points up, and render it with `--length` as usual (Thunderfury,
2026-10-14: view 2, `--length 2.0` for Dragon Slayer size). Run the exe directly (Start-Process with
`-Wait`), not through `scripts\wmvx-cli.ps1`, whose `--out` collides with PowerShell's `-OutVariable`.

## Limits

- One picture rotated: no per-angle redraw; frames pointing at / away from the camera are short and keep full width.
- Harvest always shows `CWeapon\01` (client rule), so a new weapon never appears in Harvest.
- 8-bit non-interlaced PNG input (convert WebP / JPEG first; the Scene Lab reads any format the browser does).
