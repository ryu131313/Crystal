namespace Client.MirGraphics
{
    /// <summary>
    /// Weather simulation for the atmosphere ([Graphics] Atmosphere; D3D11 + enhanced lighting). Port of the web client's
    /// WeatherFx (WebClient/src/game/weatherFx.ts): one gusting wind integrated into a drift the rain (and later fog / snow)
    /// scroll by, wetness that soaks up while it rains and dries after, snow build-up and footprints, and the lightning /
    /// thunder timers. Snow and fog are drawn in Phase 3; the state is kept here so the weather does not jump when they land.
    /// Main (UI) thread only, one instance per map.
    /// </summary>
    public sealed class WeatherFx
    {
        private const long SnowBuildMs = 180000;
        private const long FootprintMs = 40000;
        private const int MaxFootprints = 600;
        private const float WetRiseS = 70f;  // time constant of soaking up
        private const float WetDryS = 150f;  // time constant of drying out
        private const float WetOnEntry = 0.55f; // a map where it was already raining before the player arrived

        /// <summary>SoundList: the MapLightning crack (SpellObject MapLightning plays it).</summary>
        public const int ThunderSound = 8301;

        public bool Snow, Rain;
        /// <summary>Integrated wind: multiply by a layer's speed (world px / s) for its drift.</summary>
        public float DriftUnit;
        /// <summary>This frame's gust factor (about 0.4..1.25).</summary>
        public float Gust = 1f;
        /// <summary>0..1: how soaked the ground is (follows the rain with a lag, dries after it).</summary>
        public float Wetness;
        /// <summary>Set by <see cref="Update"/> for the frame a lightning strike starts.</summary>
        public bool Strike;
        /// <summary>Set by <see cref="Update"/> for the frame the thunder crack plays.</summary>
        public bool Thunder;

        private readonly Random _rng;
        private object _mapKey;
        private long _snowSince, _lastNow = -1, _nextStrike = long.MaxValue, _thunderAt = long.MaxValue, _strikeAt = long.MinValue;
        private readonly Dictionary<uint, Point> _lastCell = new Dictionary<uint, Point>();

        public WeatherFx() : this(Environment.TickCount) { }
        public WeatherFx(int seed) { _rng = new Random(seed); }

        /// <summary>Once per frame (alive or not, so the wind and timers keep running). <paramref name="weather"/> =
        /// MapControl.Weather flags; <paramref name="mapKey"/> is a reference that changes with the map (M2CellInfo).</summary>
        public void Update(long now, WeatherSetting weather, object mapKey)
        {
            bool snow = (weather & WeatherSetting.Snow) != 0;
            bool rain = (weather & WeatherSetting.Rain) != 0;

            if (!ReferenceEquals(mapKey, _mapKey))
            {
                _mapKey = mapKey;
                Wetness = rain ? WetOnEntry : 0f;
                _lastCell.Clear();
                _snowSince = now;
                _nextStrike = long.MaxValue;
                _thunderAt = long.MaxValue;
            }
            if (snow && !Snow) _snowSince = now;
            if (rain && !Rain) _nextStrike = now + 4000 + _rng.Next(8000);
            if (!rain) { _nextStrike = long.MaxValue; _thunderAt = long.MaxValue; }
            Snow = snow;
            Rain = rain;

            float dt = _lastNow < 0 ? 0f : Math.Min(Math.Max(now - _lastNow, 0), 100) / 1000f;
            _lastNow = now;
            float t = now / 1000f;
            Gust = 0.8f + 0.35f * MathF.Sin(t * 0.21f) * MathF.Sin(t * 0.077f + 1.3f) + 0.12f * MathF.Sin(t * 0.93f);
            DriftUnit += Gust * dt;
            if (rain) Wetness += (0.35f + 0.65f * RainIntensity(now) - Wetness) * (1f - MathF.Exp(-dt / WetRiseS));
            else if (Wetness > 0f) Wetness = Wetness < 0.01f ? 0f : Wetness * MathF.Exp(-dt / WetDryS);

            Strike = false;
            Thunder = false;
            if (now >= _nextStrike)
            {
                Strike = true;
                _strikeAt = now;
                _thunderAt = now + 400 + _rng.Next(2200);
                _nextStrike = now + 9000 + _rng.Next(18000);
            }
            if (now >= _thunderAt) { Thunder = true; _thunderAt = long.MaxValue; }
        }

        /// <summary>0.3..1: how hard it is raining now (slow swells; 0 when dry).</summary>
        public float RainIntensity(long now)
        {
            if (!Rain) return 0f;
            float t = now / 1000f;
            return Math.Min(1f, Math.Max(0.3f, 0.62f + 0.38f * MathF.Sin(t / 37f) * MathF.Sin(t / 91f + 0.7f) + 0.08f * MathF.Sin(t / 11f)));
        }

        /// <summary>True while the ground is wet (raining, or still drying).</summary>
        public bool Wet => Rain || Wetness > 0f;

        /// <summary>Horizontal lean of falling rain per pixel of fall, from the gust.</summary>
        public float RainSlant => Math.Min(0.32f, Math.Max(0.03f, 0.25f * Gust - 0.02f));

        /// <summary>0..1 of the snow cover's build-up target.</summary>
        public float SnowCover(long now) => Snow ? 0.35f + 0.4f * Math.Min(1f, (now - _snowSince) / (float)SnowBuildMs) : 0f;

        /// <summary>The lightning strobe envelope (0..1) for the screen flash started at the last strike (420 ms, the web
        /// strike(170, 2.6, 420, 0.1) screen strobe).</summary>
        public float StrikeFlash(long now)
        {
            const float duration = 420f;
            float e = now - _strikeAt;
            if (e < 0f || e > duration) return 0f;
            float strobe = e < 50f ? 1f : e < 95f ? 0.25f : e < 150f ? 0.9f : 0.6f;
            return strobe * MathF.Pow(1f - e / duration, 1.4f);
        }

        /// <summary>1 when fresh, 0 when gone (Phase 3 snow footprints).</summary>
        public static float FootprintFade(long born, long now)
        {
            float age = (now - born) / (float)FootprintMs;
            return age >= 1f ? 0f : 1f - age * age;
        }
    }
}
