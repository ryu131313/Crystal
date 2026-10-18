# modern-icons

Builds the desktop client's modern-UI icon library from the web UI's icons (Desktop-Modern-UI-Plan, decision D4).

**Inputs** (read only)
- `WebClient/src/ui/modern/core/icons.ts`: `ICONS` (24 x 24 SVG stroke paths), `PICTURES` (emblem keys → PNG names, incl. the `gear` / `keys` aliases), `ART` (keys only, listed as not included).
- `WebClient/play/menu-icons/*.png`: the full-colour window emblems (72 x 72).

**Outputs**
- `Client/Resources/ModernIcons.Lib`: v3 `.Lib`, the layout `LibraryEditor/Graphics/MLibraryV2.cs` writes and `Client/MirGraphics/MLibrary.cs` reads; no mask, no frame table, X/Y = 0, no remove-black pass.
- `Client/MirScenes/Modern/Core/ModernIcons.cs`: generated index constants, `Names`, `Index(name)`, `Emblem(key)`, `NotIncluded` (UTF-8 BOM, CRLF). Do not edit it by hand.

**Index layout**
- Stroke icon *i* (in `ICONS` order): image `2i` = 1x (24 x 24, stroke 2), image `2i + 1` = 2x (48 x 48, stroke 4). White on transparent, antialiased (colour 255,255,255 with the coverage in alpha), round caps and joins, like CSS `svg.ic`: tint with the sprite colour when drawing.
- Then one image per emblem PNG, sorted by file name, native size, full colour. Only one size: a 2x copy would be upscaled.
- `ART` emblems (SVG with gradients: Crafting, resume, logout, exit) are not in the library; `ModernIcons.NotIncluded` lists them.

Indices shift when an icon is added in the middle of `ICONS`, so always use the generated constants / `Index()`, never literal numbers, and ship the `.Lib` and `ModernIcons.cs` from the same run.

## When to rerun

Whenever `icons.ts` gains, loses or changes an icon (or `PICTURES`), or `play/menu-icons/` changes. Commit the regenerated `.Lib` and `.cs` together, and add `Data\ModernIcons.Lib` to the next client patch (client.md §5.3).

## Usage

```
dotnet build C:\Mir2\tools\modern-icons\ModernIcons.csproj
C:\Mir2\tools\modern-icons\bin\Debug\net10.0-windows7.0\ModernIcons.exe [--repo C:\Mir2] [--lib <out.Lib>] [--cs <out.cs>]
```

Defaults write the two outputs above (the repo root is found from the current directory or the exe). Exit code 2 = something failed to parse (listed as `FAILED ...`; the failed icon is skipped). Output under `Build\` is refused.

Check a library (reads it the way `MLibrary.cs` does, decodes every image, writes the chosen indices plus `sheet.png` with every image on grey):

```
ModernIcons.exe --verify C:\Mir2\Client\Resources\ModernIcons.Lib --out <scratch dir> [index ...]
```

## Notes

- The SVG path parser (`SvgPath.cs`) supports M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z, implicit repeats, compact numbers and arc flags. Arcs become cubic Béziers.
- Subpaths with no extent (`M7 11h.01`, `M18 10h0`) are drawn as round dots of the stroke width, as browsers do with round caps.
- Not in `Legend of Mir.sln`; builds to its own `bin`. Never point it at `Build\`.
