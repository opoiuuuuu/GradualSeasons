using System;
using System.Collections.Generic;
using System.Globalization;
using StardewValley;

namespace GradualSeasons
{
    /// <summary>Which seasonal hand-over a transition belongs to.</summary>
    public enum TransitionKind
    {
        /// <summary>No transition.</summary>
        None,

        /// <summary>Spring handing over to summer.</summary>
        SpringToSummer,

        /// <summary>Summer handing over to fall.</summary>
        SummerToFall,

        /// <summary>Fall handing over to winter.</summary>
        FallToWinter,

        /// <summary>Winter handing over to spring.</summary>
        WinterToSpring
    }

    /// <summary>Which half of a transition a date is in.</summary>
    public enum TransitionPhase
    {
        /// <summary>No transition: pure vanilla.</summary>
        None,

        /// <summary>The last days of a season, while the next season's effects rise.</summary>
        Ramp,

        /// <summary>The first days of a season, while the previous season's effects fade out.</summary>
        Linger
    }

    /// <summary>The transition state for one calendar date.</summary>
    public readonly struct TransitionInfo
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The season of the date this info describes.</summary>
        public Season Season { get; }

        /// <summary>The day of month of the date this info describes.</summary>
        public int Day { get; }

        /// <summary>Which hand-over is happening (e.g. fall to winter), or <see cref="TransitionKind.None"/>.</summary>
        public TransitionKind Kind { get; }

        /// <summary>Whether the next season is creeping in (<see cref="TransitionPhase.Ramp"/>) or the previous one is lingering (<see cref="TransitionPhase.Linger"/>).</summary>
        public TransitionPhase Phase { get; }

        /// <summary>How strong the other season's effects are on this date, from 0 to 1 (before the <see cref="ModConfig.Strength"/> multiplier).</summary>
        public float Intensity { get; }

        /// <summary>The season being left.</summary>
        public Season FromSeason { get; }

        /// <summary>The season being entered.</summary>
        public Season ToSeason { get; }

        /// <summary>Which day of the ramp or linger this is (1-based), or 0 if there's no transition.</summary>
        public int Step { get; }

        /// <summary>How many days the ramp or linger lasts, or 0 if there's no transition.</summary>
        public int Length { get; }

        /// <summary>Whether any transition effect applies on this date.</summary>
        public bool IsActive => this.Phase != TransitionPhase.None && this.Intensity > 0f;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        public TransitionInfo(Season season, int day, TransitionKind kind, TransitionPhase phase, float intensity, Season fromSeason, Season toSeason, int step, int length)
        {
            this.Season = season;
            this.Day = day;
            this.Kind = kind;
            this.Phase = phase;
            this.Intensity = intensity;
            this.FromSeason = fromSeason;
            this.ToSeason = toSeason;
            this.Step = step;
            this.Length = length;
        }

        /// <summary>Get an instance which represents a date without any transition.</summary>
        /// <param name="season">The date's season.</param>
        /// <param name="day">The date's day of month.</param>
        public static TransitionInfo NoneFor(Season season, int day)
        {
            return new TransitionInfo(season, day, TransitionKind.None, TransitionPhase.None, 0f, season, season, 0, 0);
        }

        /// <summary>Get a short label like "late Fall" or "early Winter".</summary>
        public string ShortLabel()
        {
            return this.Phase switch
            {
                TransitionPhase.Ramp => $"late {this.FromSeason}",
                TransitionPhase.Linger => $"early {this.ToSeason}",
                _ => "mid-season"
            };
        }

        /// <summary>Get a human-readable description for the SMAPI console.</summary>
        public string Describe()
        {
            return this.Phase switch
            {
                TransitionPhase.Ramp => $"late {this.FromSeason} ({this.ToSeason} creeping in, ramp day {this.Step} of {this.Length}), intensity {Fmt.Num(this.Intensity)}",
                TransitionPhase.Linger => $"early {this.ToSeason} ({this.FromSeason} lingering, linger day {this.Step} of {this.Length}), intensity {Fmt.Num(this.Intensity)}",
                _ => "no transition (pure vanilla)"
            };
        }
    }

    /// <summary>Calculates how far into a seasonal transition a date is.</summary>
    public static class TransitionSchedule
    {
        /*********
        ** Fields
        *********/
        /// <summary>The number of days in every Stardew Valley season.</summary>
        public const int DaysPerSeason = WorldDate.DaysPerMonth;

        /// <summary>The next season's intensity on the first ramp day (day 22 by default).</summary>
        public const float RampStartIntensity = 0.15f;

        /// <summary>The next season's intensity on the last day of the season (day 28).</summary>
        public const float RampEndIntensity = 1.0f;

        /// <summary>The previous season's intensity on day 1 of the new season.</summary>
        public const float LingerStartIntensity = 0.6f;

        /// <summary>The previous season's intensity on the last linger day (day 3 by default).</summary>
        public const float LingerEndIntensity = 0.15f;


        /*********
        ** Public methods
        *********/
        /// <summary>Get the transition state for a date.</summary>
        /// <param name="season">The date's season.</param>
        /// <param name="dayOfMonth">The date's day of month (1 to 28).</param>
        /// <param name="rampDays">How many days at the end of a season the next season creeps in.</param>
        /// <param name="lingerDays">How many days at the start of a season the previous season lingers.</param>
        public static TransitionInfo Get(Season season, int dayOfMonth, int rampDays, int lingerDays)
        {
            rampDays = Math.Clamp(rampDays, 0, ModConfig.MaxRampDays);
            lingerDays = Math.Clamp(lingerDays, 0, ModConfig.MaxLingerDays);
            if (dayOfMonth < 1 || dayOfMonth > DaysPerSeason)
                return TransitionInfo.NoneFor(season, dayOfMonth);

            // ramp: the last N days of the season, rising from 0.15 to 1.0
            int firstRampDay = DaysPerSeason - rampDays + 1;
            if (rampDays > 0 && dayOfMonth >= firstRampDay)
            {
                int step = dayOfMonth - firstRampDay + 1; // 1..rampDays
                float progress = rampDays == 1 ? 1f : (step - 1) / (float)(rampDays - 1);
                float intensity = Lerp(RampStartIntensity, RampEndIntensity, progress);
                Season next = Next(season);
                return new TransitionInfo(season, dayOfMonth, KindStartingFrom(season), TransitionPhase.Ramp, intensity, season, next, step, rampDays);
            }

            // linger: the first N days of the season, fading from 0.6 to 0.15
            if (lingerDays > 0 && dayOfMonth <= lingerDays)
            {
                int step = dayOfMonth; // 1..lingerDays
                float progress = lingerDays == 1 ? 0f : (step - 1) / (float)(lingerDays - 1);
                float intensity = Lerp(LingerStartIntensity, LingerEndIntensity, progress);
                Season previous = Previous(season);
                return new TransitionInfo(season, dayOfMonth, KindStartingFrom(previous), TransitionPhase.Linger, intensity, previous, season, step, lingerDays);
            }

            return TransitionInfo.NoneFor(season, dayOfMonth);
        }

        /// <summary>Get the transition state for a date.</summary>
        /// <param name="date">The date to check.</param>
        /// <param name="config">The mod settings.</param>
        public static TransitionInfo Get(WorldDate date, ModConfig config)
        {
            return Get(date.Season, date.DayOfMonth, config.RampDays, config.LingerDays);
        }

        /// <summary>Get the transition state for a date in a calendar where each season lasts several 28-day months (like Seasons of Stardew).</summary>
        /// <param name="season">The date's season.</param>
        /// <param name="dayOfMonth">The date's day of month (1 to 28).</param>
        /// <param name="month">Which month of the season it is, from 1.</param>
        /// <param name="monthsPerSeason">How many months each season lasts (1 in the base game).</param>
        /// <param name="rampDays">How many days at the end of a season the next season creeps in.</param>
        /// <param name="lingerDays">How many days at the start of a season the previous season lingers.</param>
        /// <remarks>The next season only creeps in at the end of the last month, and the previous one only lingers at the start of the first.</remarks>
        public static TransitionInfo Get(Season season, int dayOfMonth, int month, int monthsPerSeason, int rampDays, int lingerDays)
        {
            TransitionInfo info = Get(season, dayOfMonth, rampDays, lingerDays);
            if (monthsPerSeason <= 1)
                return info;

            if (info.Phase == TransitionPhase.Ramp && month < monthsPerSeason)
                return TransitionInfo.NoneFor(season, dayOfMonth);
            if (info.Phase == TransitionPhase.Linger && month > 1)
                return TransitionInfo.NoneFor(season, dayOfMonth);
            return info;
        }

        /// <summary>Get how much of the next season's look a date should have, from 0 to 1, for things that change gradually like tree colours.</summary>
        /// <param name="info">The date's transition state.</param>
        /// <param name="rampDays">How many days at the end of a season the next season creeps in.</param>
        /// <param name="lingerDays">How many days at the start of a season the previous season lingers.</param>
        /// <remarks>
        /// Unlike <see cref="TransitionInfo.Intensity"/>, this keeps rising through the whole transition window, across the season
        /// change, so nothing jumps back on day 1. With the defaults (7 + 3 days), day 22 is 1/11 of the way, day 28 is 7/11, day 1 of
        /// the new season is 8/11, and day 3 is 10/11. Days outside the window return 0.
        /// </remarks>
        public static float GetBlendShare(in TransitionInfo info, int rampDays, int lingerDays)
        {
            rampDays = Math.Clamp(rampDays, 0, ModConfig.MaxRampDays);
            lingerDays = Math.Clamp(lingerDays, 0, ModConfig.MaxLingerDays);

            int position = info.Phase switch
            {
                TransitionPhase.Ramp => info.Step,
                TransitionPhase.Linger => rampDays + info.Step,
                _ => 0
            };
            if (position <= 0)
                return 0f;

            return Math.Clamp(position / (float)(rampDays + lingerDays + 1), 0f, 1f);
        }

        /// <summary>Get the transition which starts in the given season (e.g. fall to winter for fall).</summary>
        /// <param name="season">The season being left.</param>
        public static TransitionKind KindStartingFrom(Season season)
        {
            return season switch
            {
                Season.Spring => TransitionKind.SpringToSummer,
                Season.Summer => TransitionKind.SummerToFall,
                Season.Fall => TransitionKind.FallToWinter,
                _ => TransitionKind.WinterToSpring
            };
        }

        /// <summary>Get the season after the given one.</summary>
        /// <param name="season">The current season.</param>
        public static Season Next(Season season)
        {
            return (Season)(((int)season + 1) % 4);
        }

        /// <summary>Get the season before the given one.</summary>
        /// <param name="season">The current season.</param>
        public static Season Previous(Season season)
        {
            return (Season)(((int)season + 3) % 4);
        }

        /// <summary>Parse a season name typed in the console (spring, summer, fall/autumn, winter).</summary>
        /// <param name="text">The text to parse.</param>
        /// <param name="season">The parsed season, if valid.</param>
        public static bool TryParseSeason(string? text, out Season season)
        {
            switch (text?.Trim().ToLowerInvariant())
            {
                case "spring":
                    season = Season.Spring;
                    return true;

                case "summer":
                    season = Season.Summer;
                    return true;

                case "fall":
                case "autumn":
                    season = Season.Fall;
                    return true;

                case "winter":
                    season = Season.Winter;
                    return true;

                default:
                    season = Season.Spring;
                    return false;
            }
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Linearly interpolate between two values.</summary>
        private static float Lerp(float from, float to, float progress)
        {
            return from + (to - from) * Math.Clamp(progress, 0f, 1f);
        }
    }

    /// <summary>Formats numbers for log messages the same way regardless of the player's language settings.</summary>
    internal static class Fmt
    {
        /// <summary>Format a number with two decimals, like <c>0.72</c>.</summary>
        public static string Num(float value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        /// <summary>Format a number with three decimals, like <c>0.231</c>.</summary>
        public static string Num3(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        /// <summary>Format a 0-1 chance as a percentage, like <c>57.4%</c>.</summary>
        public static string Percent(float chance)
        {
            return (chance * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
    }

    /// <summary>The in-game calendar, including mods that make each season last several 28-day months.</summary>
    /// <remarks>
    /// Seasons of Stardew (MoYu.SeasonsOfStardew) makes each season three months long: the day of the month still runs from 1 to
    /// 28, three times, and the season only changes after the third month. It stores the month on the main player as
    /// <c>MoYu.SeasonsOfStardew/Month</c>. It also clears snow on any day that isn't in winter, so snow days can't be added then.
    /// </remarks>
    internal static class SeasonCalendar
    {
        /// <summary>The unique ID of Seasons of Stardew.</summary>
        public const string SeasonsOfStardewId = "MoYu.SeasonsOfStardew";

        /// <summary>Where Seasons of Stardew stores the month of the season (1 to 3), in the main player's mod data.</summary>
        private const string SeasonsOfStardewMonthKey = "MoYu.SeasonsOfStardew/Month";

        /// <summary>How many months Seasons of Stardew gives each season.</summary>
        private const int SeasonsOfStardewMonths = 3;

        /// <summary>How many months each season lasts (1 in the base game).</summary>
        public static int MonthsPerSeason { get; private set; } = 1;

        /// <summary>Whether another mod clears snow on days outside winter, so the weather mustn't be turned into snow then.</summary>
        public static bool SnowOnlyInWinter { get; private set; }

        /// <summary>Check which calendar mods are installed. Call this once the mods are loaded.</summary>
        /// <param name="registry">The mod registry.</param>
        public static void Init(StardewModdingAPI.IModRegistry registry)
        {
            bool seasonsOfStardew = registry.IsLoaded(SeasonsOfStardewId);
            MonthsPerSeason = seasonsOfStardew ? SeasonsOfStardewMonths : 1;
            SnowOnlyInWinter = seasonsOfStardew;
        }

        /// <summary>Get which month of the season it is (always 1 in the base game).</summary>
        public static int GetMonth()
        {
            if (MonthsPerSeason <= 1)
                return 1;

            string? value = null;
            if (Game1.MasterPlayer?.modData?.TryGetValue(SeasonsOfStardewMonthKey, out value) == true && int.TryParse(value, out int month))
                return Math.Clamp(month, 1, MonthsPerSeason);
            return 1;
        }

        /// <summary>Get today's transition state.</summary>
        /// <param name="config">The mod settings.</param>
        public static TransitionInfo GetToday(ModConfig config)
        {
            return TransitionSchedule.Get(Game1.season, Game1.dayOfMonth, GetMonth(), MonthsPerSeason, config.RampDays, config.LingerDays);
        }

        /// <summary>Get tomorrow's date and transition state.</summary>
        /// <param name="config">The mod settings.</param>
        /// <param name="tomorrow">Tomorrow's date, as the game will see it.</param>
        public static TransitionInfo GetTomorrow(ModConfig config, out WorldDate tomorrow)
        {
            int month = GetMonth();
            tomorrow = new WorldDate(Game1.year, Game1.season, Game1.dayOfMonth);

            // another month of the same season
            if (Game1.dayOfMonth >= WorldDate.DaysPerMonth && month < MonthsPerSeason)
            {
                tomorrow = new WorldDate(Game1.year, Game1.season, 1);
                return TransitionSchedule.Get(Game1.season, 1, month + 1, MonthsPerSeason, config.RampDays, config.LingerDays);
            }

            tomorrow.TotalDays++;
            int tomorrowMonth = Game1.dayOfMonth >= WorldDate.DaysPerMonth ? 1 : month;
            return TransitionSchedule.Get(tomorrow.Season, tomorrow.DayOfMonth, tomorrowMonth, MonthsPerSeason, config.RampDays, config.LingerDays);
        }

        /// <summary>Describe the month for status output, or an empty string in the base game.</summary>
        public static string DescribeMonth()
        {
            return MonthsPerSeason > 1 ? $", month {GetMonth()} of {MonthsPerSeason} (Seasons of Stardew)" : string.Empty;
        }
    }

    /// <summary>Decides which areas follow the valley's seasons, using the same data the game uses.</summary>
    /// <remarks>
    /// Many expansion mods (Sunberry Village, East Scarp and others) give their areas their own location context, mostly for
    /// pass-out rules, while keeping the valley's seasons and weather. Those areas change with the valley. An area is only left
    /// alone when the game locks its season, like Ginger Island and the Desert (both locked to summer by their context).
    /// These run every frame for the current location, so they don't allocate.
    /// </remarks>
    internal static class SeasonLock
    {
        /// <summary>The most <c>CopyWeatherFromLocation</c> links followed, in case two contexts point at each other.</summary>
        private const int MaxWeatherLinks = 8;

        /// <summary>Get whether a location never follows the valley's season changes, because its map or location context locks its season.</summary>
        /// <param name="location">The location to check.</param>
        /// <remarks>This also catches an area locked to the season the valley has right now (like Ginger Island in summer), which won't follow the next one.</remarks>
        public static bool KeepsOwnSeason(GameLocation location)
        {
            if (location.GetSeason() != Game1.season)
                return true;

            // same checks as GameLocation.LoadSeasonOverride, for this location and the ones containing it
            for (GameLocation? current = location; current != null; current = current.GetParentLocation())
            {
                if (current.map?.Properties.TryGetValue("SeasonOverride", out xTile.ObjectModel.PropertyValue? value) == true && !string.IsNullOrWhiteSpace(value?.ToString()))
                    return true;
                if (current.GetLocationContext()?.SeasonOverride != null)
                    return true;
            }
            return false;
        }

        /// <summary>Get whether a location's weather can never bring rain, snow or wind (like a desert), so falling snow, leaves and petals would look out of place there.</summary>
        /// <param name="location">The location to check.</param>
        public static bool HasDryClimate(GameLocation location)
        {
            // follow the context the weather is copied from, like the game does each morning
            string contextId = location.GetLocationContextId();
            if (Game1.locationContextData is null || !Game1.locationContextData.TryGetValue(contextId, out StardewValley.GameData.LocationContexts.LocationContextData? context) || context is null)
                return false;
            for (int i = 0; i < MaxWeatherLinks && !string.IsNullOrWhiteSpace(context.CopyWeatherFromLocation); i++)
            {
                if (!Game1.locationContextData.TryGetValue(context.CopyWeatherFromLocation, out StardewValley.GameData.LocationContexts.LocationContextData? source) || source is null)
                    return false; // can't tell, so don't hide anything
                context = source;
            }

            return IsDryClimate(context.WeatherConditions);
        }

        /// <summary>Get whether a list of possible weathers only ever gives clear skies.</summary>
        /// <param name="weathers">The weather conditions from the location context data.</param>
        /// <remarks>Weather IDs added by other mods might be rain or snow, so they don't count as clear.</remarks>
        internal static bool IsDryClimate(List<StardewValley.GameData.LocationContexts.WeatherCondition>? weathers)
        {
            if (weathers is null)
                return true; // the game falls back to sun

            foreach (StardewValley.GameData.LocationContexts.WeatherCondition entry in weathers)
            {
                string? weather = entry?.Weather;
                if (string.IsNullOrWhiteSpace(weather))
                    continue;
                bool clear = string.Equals(weather, Game1.weather_sunny, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(weather, "sunny", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(weather, Game1.weather_festival, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(weather, Game1.weather_wedding, StringComparison.OrdinalIgnoreCase);
                if (!clear)
                    return false;
            }
            return true;
        }
    }
}
