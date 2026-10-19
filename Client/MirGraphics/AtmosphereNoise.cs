namespace Client.MirGraphics
{
    /// <summary>
    /// CPU mirror of the wet-ground noise (AtmosphereShaders Ground_PS / Atmosphere.ts): hash, value noise, fbm and the
    /// puddle test. Footfall splashes use <see cref="PuddleAt"/> to find the puddles the shader draws; keep the constants and
    /// the arithmetic (single precision, ascending powers) in step with the HLSL. Main (UI) thread only.
    /// </summary>
    internal static class AtmosphereNoise
    {
        private const float H1 = 123.34f, H2 = 456.21f, H3 = 45.32f;
        private const float F1 = 2.03f, F2 = 17.1f, F3 = 9.2f;

        private static float Fract(float v) => v - MathF.Floor(v);

        private static float Hash(float x, float y)
        {
            float px = Fract(x * H1), py = Fract(y * H2);
            float d = px * (px + H3) + py * (py + H3);
            px += d;
            py += d;
            return Fract(px * py);
        }

        private static float ValueNoise(float x, float y)
        {
            float ix = MathF.Floor(x), iy = MathF.Floor(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            float a = Hash(ix, iy), b = Hash(ix + 1f, iy), c = Hash(ix, iy + 1f), d = Hash(ix + 1f, iy + 1f);
            float m1 = a + (b - a) * ux, m2 = c + (d - c) * ux;
            return m1 + (m2 - m1) * uy;
        }

        /// <summary>Four-octave fbm, mirroring the shader's fbm(p).</summary>
        public static float Fbm(float x, float y)
        {
            float v = 0f, a = 0.5f;
            for (int i = 0; i < 4; i++)
            {
                v += a * ValueNoise(x, y);
                x = x * F1 + F2;
                y = y * F1 + F3;
                a *= 0.5f;
            }
            return v;
        }

        private static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        /// <summary>0..1 how much puddle the floor is at world pixel (wx, wy): the shader's puddle(world) with wetness.</summary>
        public static float PuddleAt(float wx, float wy, float wetness)
        {
            float edge = 0.72f - 0.24f * wetness;
            return Smoothstep(edge, edge + 0.09f, Fbm(wx / 150f + 3.1f, wy / 150f + 3.1f));
        }
    }
}
