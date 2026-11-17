using System;
using StardewModdingAPI;
using StardewValley;

namespace GradualSeasons
{
    /// <summary>What the weather nudger decided on one day.</summary>
    internal sealed class NudgeRecord
    {
        /// <summary>The <see cref="WorldDate.TotalDays"/> value of the day the decision was made on.</summary>
        public int DecidedOnTotalDays { get; init; }

        /// <summary>Tomorrow's date, like "Fall 26".</summary>
        public string TomorrowDate { get; init; } = string.Empty;

        /// <summary>The vanilla forecast before the mod looked at it.</summary>
        public string OriginalForecast { get; init; } = string.Empty;

        /// <summary>The forecast after the mod's decision.</summary>
        public string FinalForecast { get; init; } = string.Empty;

        /// <summary>Whether the mod changed the forecast.</summary>
        public bool Changed { get; init; }

        /// <summary>A full explanation of the decision.</summary>
        public string Summary { get; init; } = string.Empty;
    }

    /// <summary>Changes tomorrow's valley forecast during seasonal transitions (main player only).</summary>
    internal sealed class WeatherNudger
    {
        /*********
        ** Fields
        *********/
        /// <summary>Mixed into the random seed so the mod's roll isn't correlated with the game's own rolls that use similar seeds.</summary>
        private const int RandomSalt = 0x47534E57;

        /// <summary>The transition table: which vanilla forecast may change into what, and the base chance at full intensity.</summary>
        private static readonly NudgeRule[] Rules =
        {
            // spring -> summer
            new(TransitionKind.SpringToSummer, TransitionPhase.Ramp, Game1.weather_rain, Game1.weather_lightning, 0.50f, "late spring: rain turns into a thunderstorm"),
            new(TransitionKind.SpringToSummer, TransitionPhase.Ramp, Game1.weather_debris, Game1.weather_sunny, 0.50f, "late spring: the windy pollen day calms to sunny"),

            // summer -> fall
            new(TransitionKind.SummerToFall, TransitionPhase.Ramp, Game1.weather_lightning, Game1.weather_rain, 0.50f, "late summer: the storm calms to plain rain"),
            new(TransitionKind.SummerToFall, TransitionPhase.Linger, Game1.weather_rain, Game1.weather_lightning, 0.40f, "early fall: rain still turns into a thunderstorm"),

            // fall -> winter
            new(TransitionKind.FallToWinter, TransitionPhase.Ramp, Game1.weather_rain, Game1.weather_snow, 0.80f, "late fall: rain turns to snow"),
            new(TransitionKind.FallToWinter, TransitionPhase.Ramp, Game1.weather_lightning, Game1.weather_snow, 0.80f, "late fall: the cold storm turns to snow"),
            new(TransitionKind.FallToWinter, TransitionPhase.Ramp, Game1.weather_sunny, Game1.weather_snow, 0.35f, "late fall: an early snow day instead of sun"),
            new(TransitionKind.FallToWinter, TransitionPhase.Ramp, Game1.weather_debris, Game1.weather_snow, 0.35f, "late fall: the cold wind brings snow"),

            // winter -> spring
            new(TransitionKind.WinterToSpring, TransitionPhase.Ramp, Game1.weather_snow, Game1.weather_rain, 0.70f, "late winter: the thaw turns snow to rain"),
            new(TransitionKind.WinterToSpring, TransitionPhase.Linger, Game1.weather_rain, Game1.weather_snow, 0.30f, "early spring: a late cold snap turns rain back to snow"),
            new(TransitionKind.WinterToSpring, TransitionPhase.Linger, Game1.weather_lightning, Game1.weather_snow, 0.30f, "early spring: a late cold snap turns the storm to snow")
        };

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;


        /*********
        ** Accessors
        *********/
        /// <summary>The most recent decision, if any.</summary>
        public NudgeRecord? LastDecision { get; private set; }


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public WeatherNudger(IMonitor monitor)
        {
            this.Monitor = monitor;
        }

        /// <summary>Forget the last decision (e.g. when returning to the title screen).</summary>
        public void Reset()
        {
            this.LastDecision = null;
        }

        /// <summary>Decide whether to change tomorrow's forecast. Call this from the <c>DayStarted</c> event.</summary>
        /// <param name="config">The mod settings.</param>
        public void OnDayStarted(ModConfig config)
        {
            // only the main player decides the weather; farmhands receive it through the synced world state
            if (!Context.IsMainPlayer)
            {
                this.Monitor.Log("Not the main player, so tomorrow's weather is left to the host (it syncs automatically).", LogLevel.Trace);
                return;
            }

            WorldDate today = new(Game1.year, Game1.season, Game1.dayOfMonth);
            TransitionInfo info = SeasonCalendar.GetTomorrow(config, out WorldDate tomorrow);
            string original = Game1.weatherForTomorrow ?? string.Empty;
            string tomorrowText = $"{tomorrow.Season} {tomorrow.DayOfMonth}";
            string prefix = $"Tomorrow ({tomorrowText}, {info.Describe()}): vanilla forecast '{original}'.";

            // never touch these days
            string? skipReason = GetSkipReason(config, tomorrow, info, original);
            if (skipReason != null)
            {
                this.Record(config, today, tomorrowText, original, original, changed: false, $"{prefix} Left alone: {skipReason}.");
                return;
            }

            // find the rule for this forecast
            NudgeRule? rule = FindRule(info, original);
            if (rule is null)
            {
                this.Record(config, today, tomorrowText, original, original, changed: false, $"{prefix} No {info.ShortLabel()} rule for '{original}', so it stays.");
                return;
            }

            // another mod (like Seasons of Stardew) clears snow outside winter, so a snow day would just become a dry day
            if (rule.To == Game1.weather_snow && tomorrow.Season != Season.Winter && SeasonCalendar.SnowOnlyInWinter)
            {
                this.Record(config, today, tomorrowText, original, original, changed: false, $"{prefix} Rule \"{rule.Description}\" skipped: Seasons of Stardew clears snow outside winter, so it stays '{original}'.");
                return;
            }

            // roll the dice (seeded, so reloading the save gives the same result)
            float chance = Math.Clamp(rule.Chance * info.Intensity * config.Strength, 0f, 1f);
            double roll = CreateRandom().NextDouble();
            string math = $"Rule \"{rule.Description}\": {Fmt.Percent(rule.Chance)} x intensity {Fmt.Num(info.Intensity)} x strength {Fmt.Num(config.Strength)} = {Fmt.Percent(chance)} chance, rolled {Fmt.Num3(roll)}";
            if (roll >= chance)
            {
                this.Record(config, today, tomorrowText, original, original, changed: false, $"{prefix} {math}, so it stays '{original}'.");
                return;
            }

            // skip if the game would override the new value on that date anyway
            string vanillaResult = Game1.getWeatherModificationsForDate(tomorrow, rule.To);
            if (vanillaResult != rule.To)
            {
                this.Record(config, today, tomorrowText, original, original, changed: false, $"{prefix} {math}, but the game forces '{vanillaResult}' on that date anyway, so it stays '{original}'.");
                return;
            }

            // apply it exactly like the vanilla Rain Totem does in 1.6 (Object.rainTotem), so the TV forecast matches
            // and the synced world state carries it to farmhands. This only touches the valley ("Default" context);
            // Ginger Island and the Desert have their own weather, which is never changed.
            Game1.netWorldState.Value.WeatherForTomorrow = Game1.weatherForTomorrow = rule.To;

            this.Record(config, today, tomorrowText, original, rule.To, changed: true, $"{prefix} {math}, so it changed from '{original}' to '{rule.To}'.");
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get why tomorrow's forecast must be left alone, if it must.</summary>
        /// <param name="config">The mod settings.</param>
        /// <param name="tomorrow">Tomorrow's date.</param>
        /// <param name="info">Tomorrow's transition state.</param>
        /// <param name="forecast">Tomorrow's vanilla forecast.</param>
        private static string? GetSkipReason(ModConfig config, WorldDate tomorrow, in TransitionInfo info, string forecast)
        {
            if (!config.EnableWeatherChanges)
                return "weather changes are turned off in the config";

            if (tomorrow.DayOfMonth == 1)
                return "the mod never changes day 1 of a season";

            if (!info.IsActive)
                return "tomorrow isn't part of a seasonal transition";

            if (Utility.isFestivalDay(tomorrow.DayOfMonth, tomorrow.Season))
                return "tomorrow is a festival day";

            if (Utility.IsPassiveFestivalDay(tomorrow.DayOfMonth, tomorrow.Season, null))
                return "tomorrow is a passive festival day (like the Night Market)";

            if (IsWeddingScheduled(tomorrow))
                return "a wedding is scheduled for tomorrow";

            if (forecast == Game1.weather_green_rain || Utility.isGreenRainDay(tomorrow.DayOfMonth, tomorrow.Season))
                return "tomorrow is a Green Rain day";

            if (!IsBasicWeather(forecast))
                return $"'{forecast}' isn't one of the basic vanilla weathers (it may be a festival, wedding or another mod's custom weather)";

            return null;
        }

        /// <summary>Get whether a forecast is one of the five basic vanilla weathers this mod understands.</summary>
        /// <param name="weatherId">The weather ID.</param>
        private static bool IsBasicWeather(string weatherId)
        {
            return weatherId is Game1.weather_sunny or Game1.weather_rain or Game1.weather_lightning or Game1.weather_debris or Game1.weather_snow;
        }

        /// <summary>Get whether any couple (player and NPC, or two players) is getting married on or before the given date.</summary>
        /// <param name="date">The date to check.</param>
        private static bool IsWeddingScheduled(WorldDate date)
        {
            int totalDays = date.TotalDays;

            foreach (Farmer farmer in Game1.getAllFarmers())
            {
                if (farmer is null)
                    continue;

                // engaged to an NPC
                string? spouse = farmer.spouse;
                if (spouse != null && farmer.friendshipData.TryGetValue(spouse, out Friendship? npcFriendship) && IsWeddingDue(npcFriendship, totalDays))
                    return true;

                // engaged to another player
                long? playerSpouse = farmer.team.GetSpouse(farmer.UniqueMultiplayerID);
                if (playerSpouse.HasValue && IsWeddingDue(farmer.team.GetFriendship(farmer.UniqueMultiplayerID, playerSpouse.Value), totalDays))
                    return true;
            }

            return false;
        }

        /// <summary>Get whether an engagement's wedding falls on or before the given day.</summary>
        /// <param name="friendship">The friendship data.</param>
        /// <param name="totalDays">The <see cref="WorldDate.TotalDays"/> value to check.</param>
        private static bool IsWeddingDue(Friendship? friendship, int totalDays)
        {
            return
                friendship != null
                && friendship.IsEngaged()
                && friendship.WeddingDate != null
                && friendship.WeddingDate.TotalDays <= totalDays;
        }

        /// <summary>Find the rule matching a transition state and forecast, if any.</summary>
        /// <param name="info">Tomorrow's transition state.</param>
        /// <param name="forecast">Tomorrow's vanilla forecast.</param>
        private static NudgeRule? FindRule(in TransitionInfo info, string forecast)
        {
            foreach (NudgeRule rule in Rules)
            {
                if (rule.Kind == info.Kind && rule.Phase == info.Phase && rule.From == forecast)
                    return rule;
            }

            return null;
        }

        /// <summary>Create the seeded random for today's roll.</summary>
        /// <remarks>Seeded from the save's unique ID plus the number of days played, so the same day of the same save always rolls the same number.</remarks>
        private static Random CreateRandom()
        {
            unchecked
            {
                long seed = (long)Game1.uniqueIDForThisGame + Game1.stats.DaysPlayed;
                return new Random((int)(seed ^ (seed >> 32)) ^ RandomSalt);
            }
        }

        /// <summary>Save and log a decision.</summary>
        private void Record(ModConfig config, WorldDate today, string tomorrowText, string original, string final, bool changed, string summary)
        {
            this.LastDecision = new NudgeRecord
            {
                DecidedOnTotalDays = today.TotalDays,
                TomorrowDate = tomorrowText,
                OriginalForecast = original,
                FinalForecast = final,
                Changed = changed,
                Summary = summary
            };

            this.Monitor.Log(summary, config.DebugLogging ? LogLevel.Info : LogLevel.Trace);
        }


        /*********
        ** Private types
        *********/
        /// <summary>One row of the transition table.</summary>
        private sealed class NudgeRule
        {
            /// <summary>The transition this rule belongs to.</summary>
            public TransitionKind Kind { get; }

            /// <summary>Whether the rule applies while the next season creeps in, or while the previous season lingers.</summary>
            public TransitionPhase Phase { get; }

            /// <summary>The vanilla forecast this rule changes.</summary>
            public string From { get; }

            /// <summary>The forecast it changes into.</summary>
            public string To { get; }

            /// <summary>The chance at full intensity and strength (0 to 1).</summary>
            public float Chance { get; }

            /// <summary>A human-readable description for the log.</summary>
            public string Description { get; }

            /// <summary>Construct an instance.</summary>
            public NudgeRule(TransitionKind kind, TransitionPhase phase, string from, string to, float chance, string description)
            {
                this.Kind = kind;
                this.Phase = phase;
                this.From = from;
                this.To = to;
                this.Chance = chance;
                this.Description = description;
            }
        }
    }
}
