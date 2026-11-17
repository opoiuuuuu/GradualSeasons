using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace GradualSeasons
{
    /// <summary>Which leaf colours drift down.</summary>
    internal enum LeafPalette : byte
    {
        /// <summary>Late summer: orange, red and yellow leaves.</summary>
        Autumn,

        /// <summary>Early winter: the last brown and orange leaves.</summary>
        LastLeaves
    }

    /// <summary>The kinds of particle this mod draws.</summary>
    internal enum ParticleKind : byte
    {
        /// <summary>Snow flurries.</summary>
        Snow,

        /// <summary>Sleet: icy streaks mixed with small fast flakes.</summary>
        Sleet,

        /// <summary>Autumn leaves.</summary>
        Leaf,

        /// <summary>Cherry petals.</summary>
        Petal
    }

    /// <summary>How many particles of each kind should be visible.</summary>
    internal struct ParticleTargets
    {
        /// <summary>The number of snow flurries.</summary>
        public int Snow;

        /// <summary>The number of sleet particles.</summary>
        public int Sleet;

        /// <summary>The number of leaves.</summary>
        public int Leaves;

        /// <summary>The number of petals.</summary>
        public int Petals;

        /// <summary>Which leaf colours to use.</summary>
        public LeafPalette LeafPalette;

        /// <summary>The total number of particles.</summary>
        public int Total => this.Snow + this.Sleet + this.Leaves + this.Petals;
    }

    /// <summary>The vanilla weather visible in a location right now.</summary>
    internal readonly struct VanillaWeather
    {
        /// <summary>The <see cref="DebrisType"/> value when no wind debris is showing.</summary>
        public const int NoDebris = -1;

        /// <summary>Whether it's raining (including storms and Green Rain).</summary>
        public bool Raining { get; }

        /// <summary>Whether it's snowing.</summary>
        public bool Snowing { get; }

        /// <summary>Whether it's Green Rain.</summary>
        public bool GreenRain { get; }

        /// <summary>The wind debris type vanilla is showing (a <see cref="WeatherDebris"/> constant), or <see cref="NoDebris"/>.</summary>
        public int DebrisType { get; }

        /// <summary>Whether the location never shows wind debris.</summary>
        public bool IgnoresDebris { get; }

        /// <summary>Construct an instance. In the game, use <see cref="For"/> instead.</summary>
        internal VanillaWeather(bool raining, bool snowing, bool greenRain, int debrisType, bool ignoresDebris)
        {
            this.Raining = raining;
            this.Snowing = snowing;
            this.GreenRain = greenRain;
            this.DebrisType = debrisType;
            this.IgnoresDebris = ignoresDebris;
        }

        /// <summary>Read the vanilla weather for a location.</summary>
        /// <param name="location">The location to check.</param>
        public static VanillaWeather For(GameLocation location)
        {
            bool ignoresDebris = location.ignoreDebrisWeather.Value;
            int debrisType = NoDebris;
            if (!ignoresDebris && location.IsDebrisWeatherHere())
            {
                List<WeatherDebris>? debris = Game1.debrisWeather;
                debrisType = debris != null && debris.Count > 0
                    ? debris[0].which
                    : GetDebrisTypeForSeason(Game1.GetSeasonForLocation(location));
            }

            return new VanillaWeather(
                raining: location.IsRainingHere(),
                snowing: location.IsSnowingHere(),
                greenRain: location.IsGreenRainingHere(),
                debrisType: debrisType,
                ignoresDebris: ignoresDebris
            );
        }

        /// <summary>Get the debris type vanilla shows on windy days in a season (same mapping as <c>Game1.populateDebrisWeatherArray</c>).</summary>
        /// <param name="season">The season.</param>
        private static int GetDebrisTypeForSeason(Season season)
        {
            return season switch
            {
                Season.Fall => WeatherDebris.fallLeaves,
                Season.Winter => WeatherDebris.snow,
                Season.Summer => WeatherDebris.greenLeaves,
                _ => WeatherDebris.pinkPetals
            };
        }
    }

    /// <summary>Draws the cosmetic transition particles (snow flurries, sleet, leaves and petals) over the world.</summary>
    /// <remarks>Everything here is cosmetic and local: nothing is synced or saved. State is kept per screen for split-screen.</remarks>
    internal sealed class ParticleLayer
    {
        /*********
        ** Fields
        *********/
        /// <summary>How far outside the screen particles travel before wrapping to the other side, in pixels. It's larger than the widest sway plus half a sprite, so nothing pops in or out at the edges.</summary>
        private const float EdgeMargin = 72f;

        /// <summary>How long the whole layer takes to fade in after entering a location, in seconds.</summary>
        private const float LayerFadeSeconds = 1.25f;

        /// <summary>How long one particle takes to fade in or out when the particle count changes, in seconds.</summary>
        private const float ParticleFadeSeconds = 1.6f;

        /// <summary>The largest time step simulated in one frame, so a lag spike doesn't teleport particles.</summary>
        private const float MaxStepSeconds = 0.1f;

        /// <summary>The particle brightness at night.</summary>
        private const float NightBrightness = 0.6f;

        /// <summary>The particle brightness multiplier when it's raining (the world is darker).</summary>
        private const float RainBrightness = 0.85f;

        /// <summary>A full circle in radians.</summary>
        private const float TwoPi = MathF.PI * 2f;

        /// <summary>The centre of a sprite cell, used as the rotation origin.</summary>
        private static readonly Vector2 CellOrigin = new(SpriteFactory.ParticleCellSize / 2f, SpriteFactory.ParticleCellSize / 2f);

        /// <summary>The horizontal part of the sleet streak direction (the streak sprite slants 4 pixels left for every 7 down).</summary>
        private static readonly float StreakDirectionX = -4f / MathF.Sqrt(65f);

        /// <summary>The vertical part of the sleet streak direction.</summary>
        private static readonly float StreakDirectionY = 7f / MathF.Sqrt(65f);

        /// <summary>The leaf sprites for late summer.</summary>
        private static readonly byte[] AutumnLeafSprites = { SpriteFactory.LeafOrange, SpriteFactory.LeafRed, SpriteFactory.LeafYellow };

        /// <summary>The leaf sprites for early winter (brown twice as often as orange).</summary>
        private static readonly byte[] LastLeafSprites = { SpriteFactory.LeafBrown, SpriteFactory.LeafBrown, SpriteFactory.LeafOrange };

        /// <summary>The petal sprites.</summary>
        private static readonly byte[] PetalSprites = { SpriteFactory.PetalLight, SpriteFactory.PetalDeep };

        /// <summary>The particle state for each split-screen player.</summary>
        private readonly PerScreen<ScreenState> Screens = new(() => new ScreenState());

        /// <summary>Incremented to make every screen reset its particles on its next frame.</summary>
        private int Generation;


        /*********
        ** Public methods
        *********/
        /// <summary>Reset the particles on every screen (e.g. when returning to the title screen).</summary>
        public void ResetAll()
        {
            this.Generation++;
        }

        /// <summary>Stop drawing on the current screen. The particles fade back in the next time they're drawn.</summary>
        public void Hide()
        {
            ScreenState state = this.Screens.Value;
            state.Location = null;
            state.HasViewport = false;
            state.LayerAlpha = 0f;
        }

        /// <summary>Update and draw the particles for the current screen. Call this from <c>Display.RenderedWorld</c>.</summary>
        /// <param name="spriteBatch">The sprite batch being drawn.</param>
        /// <param name="texture">The particle sprite sheet.</param>
        /// <param name="info">The transition state whose particles to show.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="location">The current location (outdoors, in the valley).</param>
        /// <param name="enabled">Whether particles are wanted at all; if not, any visible particles fade out.</param>
        public void Draw(SpriteBatch spriteBatch, Texture2D texture, in TransitionInfo info, ModConfig config, GameLocation location, bool enabled)
        {
            ScreenState state = this.Screens.Value;
            this.Prepare(state, config.MaxParticles);

            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            float width = viewport.Width;
            float height = viewport.Height;
            if (width <= 0f || height <= 0f)
                return;

            // simulated time
            GameTime? gameTime = Game1.currentGameTime;
            float elapsed = Game1.paused || gameTime is null ? 0f : (float)gameTime.ElapsedGameTime.TotalSeconds;
            float deltaTime = Math.Clamp(elapsed, 0f, MaxStepSeconds);

            // what should be visible
            VanillaWeather weather = VanillaWeather.For(location);
            ParticleTargets targets = enabled ? ComputeTargets(info, config, weather) : default;
            state.Targets = targets;
            if (targets.Leaves > 0)
                state.LeafPalette = targets.LeafPalette;

            // entered a new location: scatter the particles and fade the layer in
            if (!ReferenceEquals(state.Location, location))
            {
                state.Location = location;
                state.HasViewport = false;
                state.LayerAlpha = 0f;
                if (state.ActiveCount > 0)
                {
                    Scatter(state.Snow, state.Random, width, height);
                    Scatter(state.Sleet, state.Random, width, height);
                    Scatter(state.Leaves, state.Random, width, height);
                    Scatter(state.Petals, state.Random, width, height);
                }
            }

            // follow the camera so particles feel attached to the world
            float cameraX = 0f;
            float cameraY = 0f;
            if (state.HasViewport)
            {
                cameraX = viewport.X - state.ViewportX;
                cameraY = viewport.Y - state.ViewportY;

                // a jump of more than a screen is a teleport, not camera movement
                if (MathF.Abs(cameraX) > width || MathF.Abs(cameraY) > height)
                {
                    cameraX = 0f;
                    cameraY = 0f;
                }
            }
            state.ViewportX = viewport.X;
            state.ViewportY = viewport.Y;
            state.HasViewport = true;

            // nothing to show and nothing fading out
            if (targets.Total == 0 && state.ActiveCount == 0)
                return;

            state.LayerAlpha = MathF.Min(1f, state.LayerAlpha + deltaTime / LayerFadeSeconds);
            state.Clock += deltaTime;
            if (state.Clock > 3600f)
                state.Clock -= 3600f;

            StepContext context = new(spriteBatch, texture, state, deltaTime, cameraX, cameraY, width, height, GetWind(state.Clock), GetTint(location, weather.Raining));

            int active = 0;
            active += Step(context, state.Snow, targets.Snow, ParticleKind.Snow);
            active += Step(context, state.Sleet, targets.Sleet, ParticleKind.Sleet);
            active += Step(context, state.Leaves, targets.Leaves, ParticleKind.Leaf);
            active += Step(context, state.Petals, targets.Petals, ParticleKind.Petal);
            state.ActiveCount = active;
        }

        /// <summary>Get the particle counts on the current screen, for the status command.</summary>
        /// <param name="targets">The particle counts the current screen is aiming for.</param>
        /// <param name="active">How many particles are currently alive (including ones fading out).</param>
        public void GetScreenStatus(out ParticleTargets targets, out int active)
        {
            ScreenState state = this.Screens.Value;
            targets = state.Targets;
            active = state.ActiveCount;
        }

        /// <summary>Calculate how many particles of each kind should be visible.</summary>
        /// <param name="info">The transition state whose particles to show.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="weather">The vanilla weather visible right now.</param>
        public static ParticleTargets ComputeTargets(in TransitionInfo info, ModConfig config, in VanillaWeather weather)
        {
            ParticleTargets targets = default;
            if (!info.IsActive || config.MaxParticles <= 0 || config.Strength <= 0f || weather.GreenRain)
                return targets;

            // MaxParticles x intensity x Strength, capped at MaxParticles; leaves and petals use a third of that
            float scale = info.Intensity * config.Strength;
            int full = (int)MathF.Round(MathF.Min(config.MaxParticles, config.MaxParticles * scale));
            int third = (int)MathF.Round(full / 3f);

            bool raining = weather.Raining;
            bool snowing = weather.Snowing;
            bool dry = !raining && !snowing;

            switch (info.Kind)
            {
                case TransitionKind.SpringToSummer:
                    // early summer: a few cherry petals still drift on dry days
                    if (info.Phase == TransitionPhase.Linger && !raining)
                        targets.Petals = third;
                    break;

                case TransitionKind.SummerToFall:
                    // late summer: orange, yellow and red leaves start drifting, more each day
                    if (info.Phase == TransitionPhase.Ramp)
                    {
                        targets.Leaves = third;
                        targets.LeafPalette = LeafPalette.Autumn;
                    }
                    break;

                case TransitionKind.FallToWinter:
                    if (info.Phase == TransitionPhase.Ramp)
                    {
                        // late fall: sleet over the rain on rainy days, snow flurries on dry days
                        if (raining)
                            targets.Sleet = full;
                        else if (dry)
                            targets.Snow = full;
                    }
                    else if (!snowing)
                    {
                        // early winter: the last brown and orange leaves on days without snow
                        targets.Leaves = third;
                        targets.LeafPalette = LeafPalette.LastLeaves;
                    }
                    break;

                case TransitionKind.WinterToSpring:
                    if (info.Phase == TransitionPhase.Ramp)
                    {
                        // late winter: sleet mixed into the rain
                        if (raining)
                            targets.Sleet = full;
                    }
                    else if (dry)
                    {
                        // early spring: light flurries on dry days, fading out
                        targets.Snow = full;
                    }
                    break;
            }

            // never add a particle type vanilla is already showing
            if (snowing || weather.DebrisType == WeatherDebris.snow)
                targets.Snow = 0;
            if (snowing)
                targets.Sleet = 0;
            if (weather.DebrisType == WeatherDebris.fallLeaves || weather.DebrisType == WeatherDebris.greenLeaves)
                targets.Leaves = 0;
            if (weather.DebrisType == WeatherDebris.pinkPetals)
                targets.Petals = 0;

            // locations which never show wind debris don't get leaves or petals either
            if (weather.IgnoresDebris)
            {
                targets.Leaves = 0;
                targets.Petals = 0;
            }

            return targets;
        }

        /// <summary>Describe what a transition looks like, for the console commands.</summary>
        /// <param name="info">The transition state.</param>
        public static string DescribeEffect(in TransitionInfo info)
        {
            return (info.Kind, info.Phase) switch
            {
                (TransitionKind.SpringToSummer, TransitionPhase.Ramp) => "no particles (late spring only nudges the weather)",
                (TransitionKind.SpringToSummer, TransitionPhase.Linger) => "a few cherry petals on dry days",
                (TransitionKind.SummerToFall, TransitionPhase.Ramp) => "orange, red and yellow leaves drifting down",
                (TransitionKind.SummerToFall, TransitionPhase.Linger) => "no particles (early fall only nudges the weather)",
                (TransitionKind.FallToWinter, TransitionPhase.Ramp) => "snow flurries on dry days, sleet over the rain on rainy days",
                (TransitionKind.FallToWinter, TransitionPhase.Linger) => "the last brown and orange leaves on days without snow",
                (TransitionKind.WinterToSpring, TransitionPhase.Ramp) => "sleet mixed into the rain on rainy days",
                (TransitionKind.WinterToSpring, TransitionPhase.Linger) => "light snow flurries on dry days",
                _ => "nothing (no transition)"
            };
        }

        /// <summary>Describe particle counts, for the console commands.</summary>
        /// <param name="targets">The particle counts.</param>
        public static string DescribeTargets(in ParticleTargets targets)
        {
            StringBuilder text = new();
            Append(text, targets.Snow, "snow flurries");
            Append(text, targets.Sleet, "sleet (flakes and icy streaks)");
            Append(text, targets.Leaves, targets.LeafPalette == LeafPalette.Autumn ? "orange/red/yellow leaves" : "brown/orange leaves");
            Append(text, targets.Petals, "cherry petals");
            return text.Length > 0 ? text.ToString() : "none";

            static void Append(StringBuilder builder, int count, string label)
            {
                if (count <= 0)
                    return;
                if (builder.Length > 0)
                    builder.Append(", ");
                builder.Append(count).Append(' ').Append(label);
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Reset or resize the screen's particle pools if needed. This only allocates when the max particle setting changes.</summary>
        /// <param name="state">The screen state.</param>
        /// <param name="maxParticles">The max particles setting.</param>
        private void Prepare(ScreenState state, int maxParticles)
        {
            if (state.Generation != this.Generation)
            {
                state.Generation = this.Generation;
                state.Location = null;
                state.HasViewport = false;
                state.LayerAlpha = 0f;
                state.ActiveCount = 0;
                state.Targets = default;
                Array.Clear(state.Snow, 0, state.Snow.Length);
                Array.Clear(state.Sleet, 0, state.Sleet.Length);
                Array.Clear(state.Leaves, 0, state.Leaves.Length);
                Array.Clear(state.Petals, 0, state.Petals.Length);
            }

            maxParticles = Math.Max(0, maxParticles);
            if (state.Capacity != maxParticles)
            {
                int third = (maxParticles + 2) / 3;
                state.Snow = new Particle[maxParticles];
                state.Sleet = new Particle[maxParticles];
                state.Leaves = new Particle[third];
                state.Petals = new Particle[third];
                state.Capacity = maxParticles;
                state.ActiveCount = 0;
            }
        }

        /// <summary>Update and draw one pool of particles.</summary>
        /// <param name="context">The values shared by every particle this frame.</param>
        /// <param name="pool">The particles.</param>
        /// <param name="wanted">How many particles should be visible; the rest fade out.</param>
        /// <param name="kind">The kind of particle in this pool.</param>
        /// <returns>Returns the number of particles still alive.</returns>
        private static int Step(in StepContext context, Particle[] pool, int wanted, ParticleKind kind)
        {
            ScreenState state = context.State;
            Random random = state.Random;
            float deltaTime = context.DeltaTime;
            float width = context.Width;
            float height = context.Height;
            float spanX = width + EdgeMargin * 2f;
            float spanY = height + EdgeMargin * 2f;
            float fadeStep = deltaTime / ParticleFadeSeconds;
            int alive = 0;

            for (int i = 0; i < pool.Length; i++)
            {
                ref Particle particle = ref pool[i];
                bool want = i < wanted;

                // spawn anywhere on screen, invisible, then fade in (no popping)
                if (!particle.Active)
                {
                    if (!want)
                        continue;

                    Randomize(ref particle, kind, state);
                    particle.X = RandomRange(random, -EdgeMargin, width + EdgeMargin);
                    particle.Y = RandomRange(random, -EdgeMargin, height + EdgeMargin);
                    particle.Alpha = 0f;
                    particle.Active = true;
                }

                // fade in or out
                if (want)
                    particle.Alpha = MathF.Min(1f, particle.Alpha + fadeStep);
                else
                {
                    particle.Alpha -= fadeStep;
                    if (particle.Alpha <= 0f)
                    {
                        particle.Alpha = 0f;
                        particle.Active = false;
                        continue;
                    }
                }
                alive++;

                // stay put in the world while the camera moves
                particle.X -= context.CameraX;
                particle.Y -= context.CameraY;

                // move
                switch (kind)
                {
                    case ParticleKind.Snow:
                        particle.SwayPhase = WrapAngle(particle.SwayPhase + particle.SwayFrequency * TwoPi * deltaTime);
                        particle.X += (particle.VelocityX + context.Wind) * deltaTime;
                        particle.Y += particle.VelocityY * deltaTime;
                        break;

                    case ParticleKind.Sleet:
                        particle.SwayPhase = WrapAngle(particle.SwayPhase + particle.SwayFrequency * TwoPi * deltaTime);
                        particle.X += (particle.VelocityX + context.Wind * 0.3f) * deltaTime;
                        particle.Y += particle.VelocityY * deltaTime;
                        break;

                    default: // leaves and petals flutter: they sink fastest in the middle of each swing
                        particle.SwayPhase = WrapAngle(particle.SwayPhase + particle.SwayFrequency * TwoPi * deltaTime);
                        particle.FlipPhase = WrapAngle(particle.FlipPhase + particle.FlipSpeed * deltaTime);
                        particle.Rotation = WrapAngle(particle.Rotation + particle.Spin * deltaTime);
                        particle.X += (particle.VelocityX + context.Wind * 1.6f) * deltaTime;
                        particle.Y += particle.VelocityY * (0.45f + 0.75f * MathF.Abs(MathF.Cos(particle.SwayPhase))) * deltaTime;
                        break;
                }

                // wrap around the screen edges; particles that fall off the bottom come back at the top with new looks
                if (particle.Y > height + EdgeMargin)
                {
                    particle.Y = Wrap(particle.Y, -EdgeMargin, spanY);
                    particle.X = RandomRange(random, -EdgeMargin, width + EdgeMargin);
                    Randomize(ref particle, kind, state);
                }
                else if (particle.Y < -EdgeMargin)
                    particle.Y = Wrap(particle.Y, -EdgeMargin, spanY);

                if (particle.X < -EdgeMargin || particle.X > width + EdgeMargin)
                    particle.X = Wrap(particle.X, -EdgeMargin, spanX);

                // draw
                float alpha = particle.Alpha * particle.Opacity * state.LayerAlpha;
                if (alpha < 0.004f)
                    continue;

                float drawX = particle.X + particle.SwayAmplitude * MathF.Sin(particle.SwayPhase);
                float drawY = particle.Y;
                if (drawX < -32f || drawX > width + 32f || drawY < -32f || drawY > height + 32f)
                    continue;

                float rotation = 0f;
                float scaleX = Game1.pixelZoom;
                SpriteEffects effects = SpriteEffects.None;
                if (kind is ParticleKind.Leaf or ParticleKind.Petal)
                {
                    // spin, tilt with the swing, and flip over (squash the width as it turns edge-on)
                    float flip = MathF.Cos(particle.FlipPhase);
                    rotation = particle.Rotation + 0.45f * MathF.Sin(particle.SwayPhase);
                    scaleX = Game1.pixelZoom * MathF.Max(0.2f, MathF.Abs(flip));
                    if (flip < 0f)
                        effects = SpriteEffects.FlipHorizontally;
                }

                // texture, position, source rectangle, colour, rotation, origin, scale, effects, layer depth
                context.SpriteBatch.Draw(
                    context.Texture,
                    new Vector2(MathF.Round(drawX), MathF.Round(drawY)),
                    SpriteFactory.Particles.GetSourceRect(particle.Sprite),
                    context.Tint * alpha,
                    rotation,
                    CellOrigin,
                    new Vector2(scaleX, Game1.pixelZoom),
                    effects,
                    0f
                );
            }

            return alive;
        }

        /// <summary>Give a particle a new random look and motion (but keep its position and fade).</summary>
        /// <param name="particle">The particle to change.</param>
        /// <param name="kind">The kind of particle.</param>
        /// <param name="state">The screen state.</param>
        private static void Randomize(ref Particle particle, ParticleKind kind, ScreenState state)
        {
            Random random = state.Random;
            particle.Rotation = 0f;
            particle.Spin = 0f;
            particle.FlipPhase = 0f;
            particle.FlipSpeed = 0f;
            particle.SwayPhase = RandomRange(random, 0f, TwoPi);

            switch (kind)
            {
                case ParticleKind.Snow:
                    {
                        // mostly small flakes; bigger ones fall a little faster
                        double size = random.NextDouble();
                        if (size < 0.55)
                        {
                            particle.Sprite = SpriteFactory.SnowSmall;
                            particle.VelocityY = RandomRange(random, 34f, 52f);
                        }
                        else if (size < 0.87)
                        {
                            particle.Sprite = SpriteFactory.SnowMedium;
                            particle.VelocityY = RandomRange(random, 44f, 64f);
                        }
                        else
                        {
                            particle.Sprite = SpriteFactory.SnowLarge;
                            particle.VelocityY = RandomRange(random, 56f, 80f);
                        }
                        particle.VelocityX = RandomRange(random, -6f, 6f);
                        particle.SwayAmplitude = RandomRange(random, 4f, 13f);
                        particle.SwayFrequency = RandomRange(random, 0.25f, 0.7f);
                        particle.Opacity = RandomRange(random, 0.75f, 1f);
                        break;
                    }

                case ParticleKind.Sleet:
                    if (random.NextDouble() < 0.65)
                    {
                        // icy streak falling fast along its slant
                        float speed = RandomRange(random, 420f, 560f);
                        particle.Sprite = SpriteFactory.SleetStreak;
                        particle.VelocityX = StreakDirectionX * speed;
                        particle.VelocityY = StreakDirectionY * speed;
                        particle.SwayAmplitude = 0f;
                        particle.SwayFrequency = 0f;
                        particle.Opacity = RandomRange(random, 0.6f, 0.9f);
                    }
                    else
                    {
                        // small wet flake falling fast at an angle
                        particle.Sprite = random.NextDouble() < 0.7 ? SpriteFactory.SnowSmall : SpriteFactory.SnowMedium;
                        particle.VelocityY = RandomRange(random, 170f, 230f);
                        particle.VelocityX = -particle.VelocityY * RandomRange(random, 0.35f, 0.5f);
                        particle.SwayAmplitude = RandomRange(random, 1f, 4f);
                        particle.SwayFrequency = RandomRange(random, 0.8f, 1.6f);
                        particle.Opacity = RandomRange(random, 0.7f, 0.9f);
                    }
                    break;

                case ParticleKind.Leaf:
                    particle.Sprite = Pick(random, state.LeafPalette == LeafPalette.Autumn ? AutumnLeafSprites : LastLeafSprites);
                    particle.VelocityY = RandomRange(random, 26f, 46f);
                    particle.VelocityX = RandomRange(random, -14f, -2f);
                    particle.SwayAmplitude = RandomRange(random, 16f, 36f);
                    particle.SwayFrequency = RandomRange(random, 0.16f, 0.38f);
                    particle.Rotation = RandomRange(random, 0f, TwoPi);
                    particle.Spin = RandomRange(random, 0.5f, 1.8f) * (random.NextDouble() < 0.5 ? -1f : 1f);
                    particle.FlipPhase = RandomRange(random, 0f, TwoPi);
                    particle.FlipSpeed = RandomRange(random, 1.2f, 3f);
                    particle.Opacity = 1f;
                    break;

                default:
                    particle.Sprite = Pick(random, PetalSprites);
                    particle.VelocityY = RandomRange(random, 16f, 30f);
                    particle.VelocityX = RandomRange(random, -12f, -2f);
                    particle.SwayAmplitude = RandomRange(random, 12f, 28f);
                    particle.SwayFrequency = RandomRange(random, 0.2f, 0.5f);
                    particle.Rotation = RandomRange(random, 0f, TwoPi);
                    particle.Spin = RandomRange(random, 0.8f, 2.4f) * (random.NextDouble() < 0.5 ? -1f : 1f);
                    particle.FlipPhase = RandomRange(random, 0f, TwoPi);
                    particle.FlipSpeed = RandomRange(random, 1.6f, 3.8f);
                    particle.Opacity = RandomRange(random, 0.85f, 1f);
                    break;
            }
        }

        /// <summary>Move every live particle in a pool to a random position (used after a warp).</summary>
        private static void Scatter(Particle[] pool, Random random, float width, float height)
        {
            for (int i = 0; i < pool.Length; i++)
            {
                ref Particle particle = ref pool[i];
                if (!particle.Active)
                    continue;

                particle.X = RandomRange(random, -EdgeMargin, width + EdgeMargin);
                particle.Y = RandomRange(random, -EdgeMargin, height + EdgeMargin);
            }
        }

        /// <summary>Get the gentle, gusting wind speed in pixels per second (negative blows left, like vanilla's wind).</summary>
        /// <param name="clock">The screen's particle clock in seconds.</param>
        private static float GetWind(float clock)
        {
            float gust = MathF.Sin(clock * 0.23f) * (0.6f + 0.4f * MathF.Sin(clock * 0.71f + 1.3f));
            return -12f + 9f * gust;
        }

        /// <summary>Get the colour to multiply particles by: dimmer in the evening and at night, and a little dimmer in the rain.</summary>
        /// <param name="location">The current location.</param>
        /// <param name="raining">Whether it's raining.</param>
        private static Color GetTint(GameLocation location, bool raining)
        {
            int now = ToMinutes(Game1.timeOfDay);
            int duskStart = ToMinutes(Game1.getStartingToGetDarkTime(location));
            int dark = ToMinutes(Game1.getTrulyDarkTime(location));

            float brightness = 1f;
            if (now >= dark)
                brightness = NightBrightness;
            else if (now > duskStart && dark > duskStart)
                brightness = 1f + (NightBrightness - 1f) * ((now - duskStart) / (float)(dark - duskStart));

            if (raining)
                brightness *= RainBrightness;

            return new Color(brightness, brightness, brightness, 1f);
        }

        /// <summary>Convert a game time like 1830 into minutes since midnight.</summary>
        private static int ToMinutes(int time)
        {
            return (time / 100) * 60 + time % 100;
        }

        /// <summary>Wrap a value into the range [min, min + span).</summary>
        private static float Wrap(float value, float min, float span)
        {
            float offset = (value - min) % span;
            if (offset < 0f)
                offset += span;
            return min + offset;
        }

        /// <summary>Keep an angle between 0 and 2π so it doesn't lose precision over a long session.</summary>
        private static float WrapAngle(float angle)
        {
            if (angle >= TwoPi || angle < 0f)
                angle = Wrap(angle, 0f, TwoPi);
            return angle;
        }

        /// <summary>Get a random number in a range.</summary>
        private static float RandomRange(Random random, float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }

        /// <summary>Pick a random value from a list.</summary>
        private static byte Pick(Random random, byte[] options)
        {
            return options[random.Next(options.Length)];
        }


        /*********
        ** Private types
        *********/
        /// <summary>One particle. This is a struct in a fixed-size array, so updating particles never allocates.</summary>
        private struct Particle
        {
            /// <summary>Whether the particle is alive.</summary>
            public bool Active;

            /// <summary>The sprite cell index.</summary>
            public byte Sprite;

            /// <summary>The horizontal screen position, before sway.</summary>
            public float X;

            /// <summary>The vertical screen position.</summary>
            public float Y;

            /// <summary>The horizontal speed in pixels per second (before wind).</summary>
            public float VelocityX;

            /// <summary>The falling speed in pixels per second.</summary>
            public float VelocityY;

            /// <summary>How far the particle sways side to side, in pixels.</summary>
            public float SwayAmplitude;

            /// <summary>How many side-to-side swings per second.</summary>
            public float SwayFrequency;

            /// <summary>The current position in the sway cycle, in radians.</summary>
            public float SwayPhase;

            /// <summary>The current rotation in radians (leaves and petals).</summary>
            public float Rotation;

            /// <summary>The spin speed in radians per second (leaves and petals).</summary>
            public float Spin;

            /// <summary>The current position in the flip cycle, in radians (leaves and petals).</summary>
            public float FlipPhase;

            /// <summary>The flip speed in radians per second (leaves and petals).</summary>
            public float FlipSpeed;

            /// <summary>The fade in/out amount, from 0 to 1.</summary>
            public float Alpha;

            /// <summary>The particle's own opacity, from 0 to 1.</summary>
            public float Opacity;
        }

        /// <summary>The particle state for one split-screen player.</summary>
        private sealed class ScreenState
        {
            /// <summary>The <see cref="ParticleLayer.Generation"/> this state was last reset for.</summary>
            public int Generation = -1;

            /// <summary>The max particles setting the pools were sized for.</summary>
            public int Capacity = -1;

            /// <summary>The snow flurries.</summary>
            public Particle[] Snow = Array.Empty<Particle>();

            /// <summary>The sleet particles.</summary>
            public Particle[] Sleet = Array.Empty<Particle>();

            /// <summary>The leaves.</summary>
            public Particle[] Leaves = Array.Empty<Particle>();

            /// <summary>The petals.</summary>
            public Particle[] Petals = Array.Empty<Particle>();

            /// <summary>The location the particles were last drawn in.</summary>
            public GameLocation? Location;

            /// <summary>Whether <see cref="ViewportX"/> and <see cref="ViewportY"/> are set.</summary>
            public bool HasViewport;

            /// <summary>The camera's horizontal position last frame.</summary>
            public int ViewportX;

            /// <summary>The camera's vertical position last frame.</summary>
            public int ViewportY;

            /// <summary>The whole layer's fade-in amount, from 0 to 1.</summary>
            public float LayerAlpha;

            /// <summary>Seconds of particle time, used for the wind.</summary>
            public float Clock;

            /// <summary>How many particles were alive after the last frame.</summary>
            public int ActiveCount;

            /// <summary>The particle counts this screen is aiming for.</summary>
            public ParticleTargets Targets;

            /// <summary>Which leaf colours new leaves use.</summary>
            public LeafPalette LeafPalette;

            /// <summary>The random number generator for particle looks and motion (cosmetic only).</summary>
            public readonly Random Random = new();
        }

        /// <summary>The values shared by every particle in one frame.</summary>
        private readonly struct StepContext
        {
            public readonly SpriteBatch SpriteBatch;
            public readonly Texture2D Texture;
            public readonly ScreenState State;
            public readonly float DeltaTime;
            public readonly float CameraX;
            public readonly float CameraY;
            public readonly float Width;
            public readonly float Height;
            public readonly float Wind;
            public readonly Color Tint;

            public StepContext(SpriteBatch spriteBatch, Texture2D texture, ScreenState state, float deltaTime, float cameraX, float cameraY, float width, float height, float wind, Color tint)
            {
                this.SpriteBatch = spriteBatch;
                this.Texture = texture;
                this.State = state;
                this.DeltaTime = deltaTime;
                this.CameraX = cameraX;
                this.CameraY = cameraY;
                this.Width = width;
                this.Height = height;
                this.Wind = wind;
                this.Tint = tint;
            }
        }
    }
}
