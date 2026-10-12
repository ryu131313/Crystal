using Client.MirGraphics;
using Client.MirGraphics.Rendering;
using Client.MirObjects;
using SlimDX;

namespace Client.MirScenes
{
    /// <summary>
    /// Enhanced lighting ([Graphics] Lighting=Enhanced, in game @light new / @light old). D3D11 only: D3D9 always draws the
    /// classic <see cref="MapControl.DrawLights"/>, which stays untouched and golden-tested.
    ///
    /// Same light sources, sizes and positions as the classic lights, drawn differently:
    /// <list type="bullet">
    /// <item>a float light map, so overlapping lights add up instead of clipping at white;</item>
    /// <item>a shader falloff instead of the GDI+ gradient textures;</item>
    /// <item>a 2x modulate composite with a soft knee, so lights above 1 brighten the scene (up to 2x);</item>
    /// <item>warm tints for torches, candles and map lamps, and flicker for fire-like lights;</item>
    /// <item>fades between day, dawn, evening and night instead of a hard switch, with tinted ambient light;</item>
    /// <item>spell lights tinted by the colour of the spell's own sprite frame.</item>
    /// </list>
    /// Everything is drawn when the map texture rebuilds; flicker and fades ask for a rebuild on the 100 ms tick
    /// (<see cref="NeedsTimedRebuild"/>), so they update at 10 Hz.
    /// </summary>
    public sealed partial class MapControl
    {
        private const long AmbientFadeMs = 4000;
        private const float FlickerAmplitude = 0.10F, StrongFlickerAmplitude = 0.16F;

        private Color _ambientFrom = Color.White, _ambientTo = Color.White;
        private long _ambientFadeStart = long.MinValue / 2;
        private CellInfo[,] _ambientMap;
        private bool _wasFading, _flickerInView;

        /// <summary>True when the enhanced lighting is selected and the backend can draw it.</summary>
        public static bool UseEnhancedLighting => Settings.Lighting == LightingMode.Enhanced && DXManager.EnhancedLightingAvailable;

        private bool AmbientFading => CMain.Time < _ambientFadeStart + AmbientFadeMs;

        private LightSetting CurrentLightSetting => Lights == LightSetting.Normal ? GameScene.Scene.Lights : Lights;

        /// <summary>Called on the 100 ms tick: flickering lights in view, or a day/night fade in progress (plus one last frame).</summary>
        private bool EnhancedLightingNeedsRebuild(out string reason)
        {
            reason = null;
            if (!UseEnhancedLighting) return false;

            LightSetting setting = CurrentLightSetting;
            UpdateAmbient(setting);

            bool fading = AmbientFading;
            bool fadeRebuild = fading || _wasFading;
            _wasFading = fading;
            if (fadeRebuild)
            {
                reason = "light-fade";
                return true;
            }

            if (_flickerInView && setting != LightSetting.Day)
            {
                reason = "light-flicker";
                return true;
            }

            return false;
        }

        /// <summary>Tracks the ambient target; a change starts a fade from the current colour, a new map snaps.</summary>
        private void UpdateAmbient(LightSetting setting)
        {
            Color target = EnhancedAmbient(setting);

            if (!ReferenceEquals(_ambientMap, M2CellInfo))
            {
                _ambientMap = M2CellInfo;
                _ambientFrom = _ambientTo = target;
                _ambientFadeStart = long.MinValue / 2;
                return;
            }

            if (target.ToArgb() == _ambientTo.ToArgb()) return;

            _ambientFrom = CurrentAmbient();
            _ambientTo = target;
            _ambientFadeStart = CMain.Time;
        }

        private Color CurrentAmbient()
        {
            long elapsed = CMain.Time - _ambientFadeStart;
            if (elapsed >= AmbientFadeMs) return _ambientTo;
            if (elapsed <= 0) return _ambientFrom;

            float t = (float)elapsed / AmbientFadeMs;
            t = t * t * (3F - 2F * t); // smoothstep
            return Color.FromArgb(255,
                (int)(_ambientFrom.R + (_ambientTo.R - _ambientFrom.R) * t),
                (int)(_ambientFrom.G + (_ambientTo.G - _ambientFrom.G) * t),
                (int)(_ambientFrom.B + (_ambientTo.B - _ambientFrom.B) * t));
        }

        /// <summary>The classic darkness colours, with a moonlit night and tinted dawn/evening.</summary>
        private Color EnhancedAmbient(LightSetting setting)
        {
            switch (setting)
            {
                case LightSetting.Night:
                    switch (MapDarkLight)
                    {
                        case 1: return Color.FromArgb(255, 20, 20, 20);
                        case 2: return Color.LightSlateGray;
                        case 3: return Color.SkyBlue;
                        case 4: return Color.Goldenrod;
                        default: return Color.FromArgb(255, 6, 8, 16);
                    }
                case LightSetting.Evening:
                    return Color.FromArgb(255, 62, 44, 34);
                case LightSetting.Dawn:
                    return Color.FromArgb(255, 44, 46, 64);
                default:
                    return Color.White;
            }
        }

        private void DrawEnhancedLights(LightSetting setting)
        {
            if (DXManager.EnhancedLightTexture == null || DXManager.EnhancedLightTexture.Disposed)
                DXManager.EnhancedLightTexture = DXManager.CreateLightTarget(Settings.ScreenWidth, Settings.ScreenHeight);

            RenderTexture oldSurface = DXManager.CurrentSurface;
            DXManager.SetSurface(DXManager.EnhancedLightTexture);

            bool blind = MapObject.User.Poison.HasFlag(PoisonType.Blindness);
            Color ambient = CurrentAmbient();
            if (blind) ambient = GetBlindLight(ambient);
            DXManager.Clear(ambient);

            // Lights fade out with the darkness: full strength from dawn/evening brightness down, none in daylight.
            float ambientLevel = Math.Max(ambient.R, Math.Max(ambient.G, ambient.B)) / 255F;
            float lightScale = Math.Clamp((1F - ambientLevel) / (1F - 64F / 255F), 0F, 1F);

            _flickerInView = false;

            if (lightScale > 0F)
            {
                DXManager.SetBlend(true);
                DXManager.SetEnhancedLightAdditiveBlend();

                DrawEnhancedObjectLights(blind, lightScale);
                DrawEnhancedMapEffectLights(blind, lightScale);
                DrawEnhancedCellLights(blind, lightScale);

                DXManager.EndEnhancedLightBlend();
                DXManager.SetBlend(false);
            }

            DXManager.SetSurface(oldSurface);

            DXManager.SetEnhancedLightCompositeBlend();
            DXManager.Draw(DXManager.EnhancedLightTexture, new Rectangle(0, 0, Settings.ScreenWidth, Settings.ScreenHeight), Vector3.Zero, Color.White);
            DXManager.RestartSpriteAlphaBlend();
        }

        private void DrawEnhancedObjectLights(bool blind, float lightScale)
        {
            for (int i = 0; i < ObjectsList.Count; i++)
            {
                MapObject ob = ObjectsList[i];
                if (ob.Light > 0 && (!ob.Dead || ob == MapObject.User || ob.Race == ObjectType.Spell))
                {
                    int light = ob.Light;
                    Color colour = ob.LightColour;
                    float intensity = 1F;
                    int flickerSeed = 0;
                    float amplitude = FlickerAmplitude;

                    if (ob.Race == ObjectType.Player)
                    {
                        switch (light / 15)
                        {
                            case 0: //no light source
                                colour = Color.FromArgb(255, 60, 60, 60);
                                break;
                            case 1:
                                colour = Color.FromArgb(255, 120, 120, 120);
                                break;
                            case 2: //Candle
                                colour = Color.FromArgb(255, 230, 190, 140);
                                intensity = 1.15F;
                                flickerSeed = (int)ob.ObjectID;
                                break;
                            case 3: //Torch
                                colour = Color.FromArgb(255, 255, 205, 150);
                                intensity = 1.3F;
                                flickerSeed = (int)ob.ObjectID;
                                break;
                            default: //Peddler Torch
                                colour = Color.FromArgb(255, 255, 215, 165);
                                intensity = 1.4F;
                                flickerSeed = (int)ob.ObjectID;
                                break;
                        }
                    }
                    else if (ob.Race == ObjectType.Merchant)
                    {
                        colour = Color.FromArgb(255, 120, 120, 120);
                    }
                    else if (ob.Race == ObjectType.Spell && ob is SpellObject spell)
                    {
                        if (spell.BodyLibrary != null && spell.BodyLibrary.TryGetLightTint(spell.DrawFrame + spell.FrameIndex, out Color tint))
                            colour = tint;
                        intensity = 1.2F;
                        flickerSeed = (int)ob.ObjectID;
                        amplitude = StrongFlickerAmplitude;
                    }

                    if (blind) colour = GetBlindLight(colour);

                    AddEnhancedLight(ob.DrawLocation, light % 15, colour, intensity * lightScale, flickerSeed, amplitude, 0);
                }

                if (!Settings.Effect) continue;
                for (int e = 0; e < ob.Effects.Count; e++)
                {
                    Effect effect = ob.Effects[e];
                    if (!effect.Blend || CMain.Time < effect.Start || (!(effect is Missile) && effect.Light < ob.Light)) continue;

                    Color colour = EffectLightColour(effect);
                    if (blind) colour = GetBlindLight(colour);

                    AddEnhancedLight(effect.DrawLocation, effect.Light, colour, 1.2F * lightScale, 0, 0F, 0);
                }
            }
        }

        private void DrawEnhancedMapEffectLights(bool blind, float lightScale)
        {
            if (!Settings.Effect) return;

            for (int e = 0; e < Effects.Count; e++)
            {
                Effect effect = Effects[e];
                if (!effect.Blend || CMain.Time < effect.Start || effect.Light == 0) continue;

                Color colour = EffectLightColour(effect);
                if (blind) colour = GetBlindLight(colour);

                AddEnhancedLight(effect.DrawLocation, effect.Light, colour, 1.2F * lightScale, 0, 0F, 0);
            }
        }

        private void DrawEnhancedCellLights(bool blind, float lightScale)
        {
            for (int y = MapObject.User.Movement.Y - ViewRangeY - 24; y <= MapObject.User.Movement.Y + ViewRangeY + 24; y++)
            {
                if (y < 0) continue;
                if (y >= Height) break;
                for (int x = MapObject.User.Movement.X - ViewRangeX - 24; x < MapObject.User.Movement.X + ViewRangeX + 24; x++)
                {
                    if (x < 0) continue;
                    if (x >= Width) break;
                    CellInfo cell = M2CellInfo[x, y];
                    int imageIndex = (cell.FrontImage & 0x7FFF) - 1;
                    if (imageIndex == -1) continue;
                    int fileIndex = cell.FrontIndex;
                    if (fileIndex == -1) continue;
                    if (cell.Light == 0 || cell.Light >= 10) continue;

                    // Light < 10 here, so the classic colour bands (Light / 10 = 1..4) never apply and every map light is
                    // white; here they are warm, flickering lamps.
                    Color colour = Color.FromArgb(255, 255, 236, 210);
                    if (blind) colour = GetBlindLight(colour);

                    Point p = new Point(
                        (x + OffSetX - MapObject.User.Movement.X) * CellWidth + MapObject.User.OffSetMove.X,
                        (y + OffSetY - MapObject.User.Movement.Y) * CellHeight + MapObject.User.OffSetMove.Y + 32);

                    if (cell.FrontAnimationFrame > 0 && fileIndex >= 0 && fileIndex < Libraries.MapLibs.Length)
                        p.Offset(Libraries.MapLibs[fileIndex].GetOffSet(imageIndex));

                    AddEnhancedLight(p, (cell.Light % 10) * 3, colour, 1.15F * lightScale, x * 7919 + y * 104729 + 1, FlickerAmplitude, 10);
                }
            }
        }

        /// <summary>An explicit (non-white) colour wins; otherwise the tint of the frame the effect drew last.</summary>
        private static Color EffectLightColour(Effect effect)
        {
            if (effect.LightColour.ToArgb() != Color.White.ToArgb() || effect.Library == null) return effect.LightColour;

            int index = effect.LastDrawnIndex >= 0 ? effect.LastDrawnIndex : effect.BaseIndex + effect.CurrentFrame;
            return effect.Library.TryGetLightTint(index, out Color tint) ? tint : effect.LightColour;
        }

        /// <summary>
        /// One light at the classic position: the classic code offsets by half of LightSizes[range] but draws the next larger
        /// texture (LightSizes[range + 1]), so the same rectangle is used here. <paramref name="flickerSeed"/> 0 = steady.
        /// </summary>
        private void AddEnhancedLight(Point location, int range, Color colour, float intensity, int flickerSeed, float amplitude, int extraX)
        {
            if (intensity <= 0F) return;

            int last = DXManager.LightSizes.Length - 2;
            if (range < 0) range = 0;
            if (range > last) range = last;

            Point offset = DXManager.LightSizes[range];
            Point size = DXManager.LightSizes[range + 1];
            float x = location.X - offset.X / 2 - CellWidth / 2 + extraX;
            float y = location.Y - offset.Y / 2 - CellHeight / 2 - 5;
            float width = size.X, height = size.Y;

            if (flickerSeed != 0)
            {
                _flickerInView = true;
                float n = Flicker(flickerSeed);
                intensity *= 1F + amplitude * n;

                float grow = 1F + 0.03F * n; // the flame breathes a little too
                float cx = x + width / 2F, cy = y + height / 2F;
                width *= grow;
                height *= grow;
                x = cx - width / 2F;
                y = cy - height / 2F;
            }

            DXManager.DrawLight(x, y, width, height, colour, intensity);
        }

        /// <summary>Smooth noise in -1..1 from three detuned sines; the seed de-phases neighbouring lights.</summary>
        private static float Flicker(int seed)
        {
            float t = (CMain.Time % 3600000L) * 0.001F;
            float s = (seed & 0xFFFF) * 0.6180339F;
            return MathF.Sin(t * 7.3F + s * 11F) * 0.5F + MathF.Sin(t * 13.1F + s * 23F) * 0.3F + MathF.Sin(t * 2.9F + s * 5F) * 0.2F;
        }
    }
}
