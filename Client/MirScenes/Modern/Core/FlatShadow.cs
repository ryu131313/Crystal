using Client.MirGraphics;
using Client.MirGraphics.Rendering;

namespace Client.MirScenes.Modern.Core
{
    /// <summary>
    /// A library frame with its baked checkerboard shadow turned into a flat translucent fill, for sprites shown large in the new
    /// UI (NPC portrait). CPU port of the web's SHADOW_FILL_PS rule (WebClient/src/render/shaders.ts, hard edge): Crystal's shadow
    /// texels are opaque near-black; one whose orthogonal neighbours are (nearly) all clear is dither and becomes the fill, a clear
    /// texel fills from its orthogonal shadow neighbours (full between two opposite ones). Dark outlines have shadow-coloured
    /// neighbours and stay as drawn. The fill is black at <see cref="FillAlpha"/> (the web's original look, 0.42).
    /// </summary>
    internal static class FlatShadow
    {
        public const byte FillAlpha = 107;

        /// <summary>A new texture (the caller owns it) the frame's size, or null when the frame has no pixels.</summary>
        public static unsafe RenderTexture Make(MLibrary lib, int index)
        {
            byte[] p = lib?.GetPixels(index, out _);
            if (p == null) return null;
            Size s = lib.GetSize(index);
            int w = s.Width, h = s.Height;
            if (w <= 0 || h <= 0 || p.Length < w * h * 4) return null;

            bool Clear(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || p[(y * w + x) * 4 + 3] < 13;
            bool Shadow(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return false;
                int i = (y * w + x) * 4;
                int hi = Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])), lo = Math.Min(p[i], Math.Min(p[i + 1], p[i + 2]));
                return p[i + 3] > 102 && hi < 41 && hi - lo < 20;
            }

            RenderTexture t = DXManager.CreateOwnedTexture(w, h);
            byte* dst = DXManager.LockTexture(t);
            try
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = (y * w + x) * 4;
                        float fill;
                        if (Shadow(x, y))
                        {
                            int clear = (Clear(x - 1, y) ? 1 : 0) + (Clear(x + 1, y) ? 1 : 0) + (Clear(x, y - 1) ? 1 : 0) + (Clear(x, y + 1) ? 1 : 0);
                            fill = clear >= 3 ? 1f : -1f;
                        }
                        else if (p[i + 3] >= 13) fill = -1f;
                        else
                        {
                            bool sl = Shadow(x - 1, y), sr = Shadow(x + 1, y), su = Shadow(x, y - 1), sd = Shadow(x, y + 1);
                            int around = (sl ? 1 : 0) + (sr ? 1 : 0) + (su ? 1 : 0) + (sd ? 1 : 0);
                            float e = Math.Clamp((around - 1.5f) / 2f, 0f, 1f);
                            fill = (sl && sr) || (su && sd) ? 1f : e * e * (3 - 2 * e);
                        }

                        if (fill < 0)
                        {
                            dst[i] = p[i]; dst[i + 1] = p[i + 1]; dst[i + 2] = p[i + 2]; dst[i + 3] = p[i + 3];
                        }
                        else
                        {
                            dst[i] = dst[i + 1] = dst[i + 2] = 0;
                            dst[i + 3] = (byte)Math.Round(fill * FillAlpha);
                        }
                    }
            }
            finally
            {
                DXManager.UnlockTexture(t);
            }
            return t;
        }
    }
}
