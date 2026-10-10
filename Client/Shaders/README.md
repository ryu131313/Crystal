# Client shaders (reference copies)

Copied 2026-10-10 from a deployed client's `Data\Shaders\` (roadmap C1). They are **not** part of the build: the
client loads them at runtime from `Settings.ShadersPath` (`Data\Shaders\`), and the patcher ships them.

| File | Used by | Notes |
|---|---|---|
| `grayscale.ps` | `DXManager.SetGrayscale` (`LoadPixelsShaders`) | ps_1_2 assembly (luma = dot(rgb, 0.3/0.59/0.11), alpha kept); the only live shader. C4 ports it to HLSL (ps_4_0). |
| `shadow_ps.glsl` | nothing in `Client/` | GLSL, never loaded by the D3D9 client. |

`normal.ps` and `magic.ps` (also referenced by `LoadPixelsShaders`) do **not** exist in the deployed client, and their
callers `SetNormal`/`SetBlendMagic` are never called: both paths are dead and need no port.
