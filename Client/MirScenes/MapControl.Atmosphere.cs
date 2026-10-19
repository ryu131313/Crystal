using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirSounds;
using SlimDX;

namespace Client.MirScenes
{
    /// <summary>
    /// Atmosphere weather ([Graphics] Atmosphere; Desktop-Smooth-Mode-and-Weather Phase 1): shader rain and lightning on
    /// weather maps. D3D11 + enhanced lighting only; D3D9 and classic lighting keep the weather sprites untouched.
    ///
    /// The simulation is <see cref="WeatherFx"/> (one per map). This partial owns it, the thunder sound, and two passes drawn
    /// every frame over the cached map texture in <see cref="DrawControl"/> (never baked into it, so rain costs no map
    /// rebuild): the rainfall, lit by the light map the cached map was built with (the same soft knee as LightComposite_PS,
    /// plus glints past white), and the lightning screen strobe.
    /// </summary>
    public sealed partial class MapControl
    {
        private WeatherFx _weatherFx;
        private readonly Vector4[] _atmoInputs = new Vector4[4];

        /// <summary>Set by <see cref="CreateTexture"/>: the cached map went through the enhanced light pass, so
        /// <see cref="DXManager.EnhancedLightTexture"/> belongs to it (on Day without a pass the texture is stale).</summary>
        private bool _atmosphereLightDrawn;

        /// <summary>True when the atmosphere may run here: [Graphics] Atmosphere, D3D11 with the shaders compiled, enhanced lighting.</summary>
        private static bool AtmosphereEnabled => Settings.Atmosphere && DXManager.AtmosphereAvailable && UseEnhancedLighting;

        /// <summary>True while the shader rain replaces the Weather.Lib rain sprites on this map.</summary>
        private bool AtmosphereRain => AtmosphereEnabled && (Weather & WeatherSetting.Rain) != 0;

        /// <summary>Once per spin (called from <see cref="Process"/>): advance the simulation, play the thunder.</summary>
        public void AtmosphereProcess()
        {
            if (!AtmosphereEnabled || M2CellInfo == null || User == null)
            {
                _weatherFx = null;
                return;
            }

            if (_weatherFx == null) _weatherFx = new WeatherFx();
            _weatherFx.Update(CMain.Time, Weather, M2CellInfo);

            if (_weatherFx.Thunder) SoundManager.PlaySound(WeatherFx.ThunderSound);
        }

        /// <summary>DrawControl, after the cached map: rain then the lightning strobe. Alive only (the dead-user grayscale
        /// keeps its own path).</summary>
        private void DrawAtmosphereOverlay()
        {
            if (_weatherFx == null || User == null || User.Dead) return;
            DrawAtmosphereRain();
            DrawAtmosphereLightning();
        }

        /// <summary>The fullscreen rainfall pass over the cached map; its source is the light map that map was lit with.</summary>
        private void DrawAtmosphereRain()
        {
            if (!AtmosphereRain) return;

            RenderTexture light = DXManager.EnhancedLightTexture;
            bool hasLight = _atmosphereLightDrawn && light != null && !light.Disposed && light != DXManager.CurrentSurface;

            float intensity = _weatherFx.RainIntensity(CMain.Time);
            float camX = (User.Movement.X - OffSetX) * CellWidth - User.OffSetMove.X;
            float camY = (User.Movement.Y - OffSetY) * CellHeight - User.OffSetMove.Y;

            _atmoInputs[0] = new Vector4(Size.Width, Size.Height, CMain.Time / 1000f, 0.5f * (0.3f + 0.7f * intensity));
            _atmoInputs[1] = new Vector4(camX, camY, _weatherFx.RainSlant, 0.07f * intensity);
            _atmoInputs[2] = new Vector4(_weatherFx.DriftUnit * 0.45f, 0.55f * (0.2f + 0.8f * intensity), hasLight ? 1f : 0f, 0f);
            DXManager.DrawFullscreen(FullscreenKind.Rainfall, hasLight ? light : null, _atmoInputs);
        }

        /// <summary>After the rain: the lightning screen strobe (colour 170,195,255).</summary>
        private void DrawAtmosphereLightning()
        {
            if (!AtmosphereEnabled || _weatherFx == null) return;

            float flash = _weatherFx.StrikeFlash(CMain.Time);
            if (flash <= 0f) return;

            _atmoInputs[0] = new Vector4(170f / 255f, 195f / 255f, 1f, flash * 0.45f);
            DXManager.DrawFullscreen(FullscreenKind.Lightning, null, _atmoInputs);
        }
    }
}
