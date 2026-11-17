using System;

namespace GradualSeasons
{
    /// <summary>The mod settings. SMAPI saves these in the mod folder as <c>config.json</c>.</summary>
    public sealed class ModConfig
    {
        /*********
        ** Limits
        *********/
        /// <summary>The largest allowed <see cref="RampDays"/> value.</summary>
        public const int MaxRampDays = 14;

        /// <summary>The largest allowed <see cref="LingerDays"/> value.</summary>
        public const int MaxLingerDays = 7;

        /// <summary>The largest allowed <see cref="Strength"/> value.</summary>
        public const float MaxStrength = 2f;

        /// <summary>The largest allowed <see cref="MaxParticles"/> value.</summary>
        public const int MaxParticleLimit = 1000;


        /*********
        ** Settings
        *********/
        /// <summary>Whether the main player's game may change tomorrow's weather forecast during a transition.</summary>
        public bool EnableWeatherChanges { get; set; } = true;

        /// <summary>Whether to draw the cosmetic transition particles (snow flurries, sleet, leaves, petals).</summary>
        public bool EnableParticles { get; set; } = true;

        /// <summary>Whether the map gradually turns into the next season during transitions (the game's own next-season ground, water, cliffs, town roofs and map trees in patches), with fallen leaves, petals and flowers on top.</summary>
        public bool EnableMapChanges { get; set; } = true;

        /// <summary>Whether trees (and the few buildings with their own seasonal look, like the pet bowl) gradually take on the next season's look: leaves turning, leaves dropping and snow on branches, buds. Town buildings are map tiles, so they follow <see cref="EnableMapChanges"/>.</summary>
        public bool EnableTreeAndBuildingChanges { get; set; } = true;

        /// <summary>Whether snow builds up on the roofs of the farmhouse and farm buildings through late fall, stays all winter and melts in early spring (the game gives them no winter look). Turn this off if another mod already gives your buildings a winter look.</summary>
        public bool EnableRoofSnow { get; set; } = true;

        /// <summary>How many days at the end of each season the next season creeps in (0 disables the ramp).</summary>
        public int RampDays { get; set; } = 7;

        /// <summary>How many days at the start of each season the previous season lingers (0 disables the linger).</summary>
        public int LingerDays { get; set; } = 3;

        /// <summary>A multiplier for every transition chance and particle count (0 = off, 1 = normal, 2 = double).</summary>
        public float Strength { get; set; } = 1.0f;

        /// <summary>The most particles of one type drawn at once (leaves and petals use a third of this).</summary>
        public int MaxParticles { get; set; } = 150;

        /// <summary>Whether to draw particles while a festival is running.</summary>
        public bool ShowDuringFestivals { get; set; } = false;

        /// <summary>Whether to log every weather decision at Info level (visible in the SMAPI console) instead of Trace (log file only).</summary>
        public bool DebugLogging { get; set; } = false;


        /*********
        ** Methods
        *********/
        /// <summary>Force every value into its allowed range.</summary>
        /// <returns>Returns whether any value was changed.</returns>
        public bool Clamp()
        {
            bool changed = false;

            int rampDays = Math.Clamp(this.RampDays, 0, MaxRampDays);
            int lingerDays = Math.Clamp(this.LingerDays, 0, MaxLingerDays);
            int maxParticles = Math.Clamp(this.MaxParticles, 0, MaxParticleLimit);
            float strength = float.IsNaN(this.Strength) || float.IsInfinity(this.Strength)
                ? 1f
                : Math.Clamp(this.Strength, 0f, MaxStrength);

            if (rampDays != this.RampDays || lingerDays != this.LingerDays || maxParticles != this.MaxParticles || !strength.Equals(this.Strength))
                changed = true;

            this.RampDays = rampDays;
            this.LingerDays = lingerDays;
            this.MaxParticles = maxParticles;
            this.Strength = strength;
            return changed;
        }
    }
}
