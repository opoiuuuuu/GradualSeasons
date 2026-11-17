using System;
using System.Globalization;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Mods;
using StardewValley.TerrainFeatures;

namespace GradualSeasons
{
    /// <summary>The mod entry point.</summary>
    public sealed class ModEntry : Mod
    {
        /*********
        ** Fields
        *********/
        /// <summary>The unique ID of Generic Mod Config Menu.</summary>
        private const string GenericModConfigMenuId = "spacechase0.GenericModConfigMenu";

        /// <summary>The mod settings.</summary>
        private ModConfig Config = new();

        /// <summary>Changes tomorrow's forecast during transitions (main player only).</summary>
        private WeatherNudger Nudger = null!;

        /// <summary>Draws the transition particles.</summary>
        private readonly ParticleLayer Particles = new();

        /// <summary>Draws the transition ground cover.</summary>
        private readonly GroundLayer Ground = new();

        /// <summary>Gradually changes trees and buildings to the next season's look.</summary>
        private TextureBlender Blender = null!;

        /// <summary>Gradually turns the map itself (ground, water, cliffs, big map trees) into the next season using the game's own tiles.</summary>
        private SeasonTileLayer Tiles = null!;

        /// <summary>The season when the date was last checked, to notice console season changes.</summary>
        private Season? LastSeason;

        /// <summary>The particle sprite sheet, or <c>null</c> if it couldn't be created.</summary>
        private Texture2D? ParticleTexture;

        /// <summary>The ground cover sprite sheet, or <c>null</c> if it couldn't be created.</summary>
        private Texture2D? GroundTexture;

        /// <summary>Today's transition state.</summary>
        private TransitionInfo Today;

        /// <summary>Tomorrow's transition state.</summary>
        private TransitionInfo Tomorrow;

        /// <summary>A number identifying the date <see cref="Today"/> was calculated for, or -1 if it hasn't been yet.</summary>
        private int LastDateKey = -1;

        /// <summary>Whether <c>gs_preview</c> is showing another date's particles.</summary>
        private bool PreviewActive;

        /// <summary>The season being previewed.</summary>
        private Season PreviewSeason;

        /// <summary>The day being previewed.</summary>
        private int PreviewDay;

        /// <summary>The transition state of the date being previewed.</summary>
        private TransitionInfo PreviewInfo;

        /// <summary>Whether a particle drawing error was already logged today (so a broken frame doesn't flood the log).</summary>
        private bool LoggedDrawError;

        /// <summary>Whether a ground cover drawing error was already logged today.</summary>
        private bool LoggedGroundError;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            this.Config = this.LoadConfig();
            this.Nudger = new WeatherNudger(this.Monitor);
            this.Blender = new TextureBlender(helper, this.Monitor);
            this.Tiles = new SeasonTileLayer(this.Monitor);

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.OneSecondUpdateTicked += this.OnOneSecondUpdateTicked;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            helper.Events.Display.RenderedStep += this.OnRenderedStep;
            helper.Events.Display.RenderingStep += this.OnRenderingStep;
            helper.Events.Content.AssetRequested += this.OnAssetRequested;
            helper.Events.Content.AssetsInvalidated += this.OnAssetsInvalidated;

            helper.ConsoleCommands.Add(
                "gs_status",
                "Shows Gradual Seasons' state: the date, today's and tomorrow's transition intensity, tomorrow's forecast, what the mod changed, and the particles and ground cover here.\n\nUsage: gs_status",
                this.OnStatusCommand
            );
            helper.ConsoleCommands.Add(
                "gs_preview",
                "Shows another date's transition particles and ground cover right now (cosmetic only; nothing is saved).\n\nUsage: gs_preview <season> <day>\n- season: spring, summer, fall or winter\n- day: 1 to 28\nExample: gs_preview fall 26\n\nUsage: gs_preview off\nStops the preview.",
                this.OnPreviewCommand
            );
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <inheritdoc cref="IGameLoopEvents.GameLaunched"/>
        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            // mods that change how long seasons last
            SeasonCalendar.Init(this.Helper.ModRegistry);
            if (SeasonCalendar.MonthsPerSeason > 1)
                this.Monitor.Log($"Seasons of Stardew is installed, so each season lasts {SeasonCalendar.MonthsPerSeason} months: the next season creeps in at the end of the last month, and snow days are only added in winter (that mod clears snow at other times).", LogLevel.Info);

            try
            {
                this.ParticleTexture = SpriteFactory.LoadOrCreate(this.Helper, this.Monitor, SpriteFactory.Particles);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't set up the particle sprites, so particles are disabled (weather changes still work). Technical details:\n{ex}", LogLevel.Error);
            }

            try
            {
                this.GroundTexture = SpriteFactory.LoadOrCreate(this.Helper, this.Monitor, SpriteFactory.Ground);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't set up the ground cover sprites, so snow patches, grass and flowers are disabled (everything else still works). Technical details:\n{ex}", LogLevel.Error);
            }

            try
            {
                this.RegisterConfigMenu();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't add the Generic Mod Config Menu page (you can still edit config.json). Technical details:\n{ex}", LogLevel.Warn);
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.SaveLoaded"/>
        private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
        {
            try
            {
                this.RefreshDates(force: true);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while loading the save. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.DayStarted"/>
        private void OnDayStarted(object? sender, DayStartedEventArgs e)
        {
            try
            {
                this.LoggedDrawError = false;
                this.LoggedGroundError = false;
                this.Tiles.ResetErrorLog();
                this.RefreshDates(force: true);
                this.Nudger.OnDayStarted(this.Config);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while checking tomorrow's weather (the forecast was left unchanged). Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.OneSecondUpdateTicked"/>
        private void OnOneSecondUpdateTicked(object? sender, OneSecondUpdateTickedEventArgs e)
        {
            try
            {
                // catches date changes from console commands like world_setday, not only new days
                if (Context.IsWorldReady)
                {
                    Season? before = this.LastSeason;
                    this.RefreshDates(force: false);
                    if (before.HasValue && before.Value != Game1.season)
                        this.RefreshSeasonalWorld();
                }
            }
            catch (Exception ex)
            {
                this.Monitor.LogOnce($"Error while checking the date. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <inheritdoc cref="IGameLoopEvents.ReturnedToTitle"/>
        private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
        {
            try
            {
                this.PreviewActive = false;
                this.LastDateKey = -1;
                this.Today = default;
                this.Tomorrow = default;
                this.Nudger.Reset();
                this.Particles.ResetAll();
                this.Ground.ResetAll();
                this.Tiles.ResetAll();
                this.Blender.Reset();
                this.LastSeason = null;
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while resetting on the title screen. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <inheritdoc cref="IDisplayEvents.RenderedWorld"/>
        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            try
            {
                this.Tiles.EndWorldDraw();

                Texture2D? texture = this.ParticleTexture;
                GameLocation? location = Game1.currentLocation;
                if (texture is null || location is null || this.GetDrawBlocker(location, weatherEffect: true) != null)
                {
                    this.Particles.Hide();
                    return;
                }

                bool enabled = this.Config.EnableParticles || this.PreviewActive;
                if (this.PreviewActive)
                    this.Particles.Draw(e.SpriteBatch, texture, this.PreviewInfo, this.Config, location, enabled);
                else
                    this.Particles.Draw(e.SpriteBatch, texture, this.Today, this.Config, location, enabled);
            }
            catch (Exception ex)
            {
                if (!this.LoggedDrawError)
                {
                    this.LoggedDrawError = true;
                    this.Monitor.Log($"Error while drawing particles (repeats are hidden until tomorrow). Technical details:\n{ex}", LogLevel.Error);
                }
            }
        }

        /// <inheritdoc cref="IDisplayEvents.RenderedStep"/>
        private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
        {
            // the map's seasonal change is drawn right after each map layer, so it only needs to know which part of the world is being drawn
            switch (e.Step)
            {
                case RenderSteps.World_Background:
                    this.Tiles.SetStep(WorldDrawStep.Buildings);
                    break;

                case RenderSteps.World_Sorted:
                    this.Tiles.SetStep(WorldDrawStep.None);
                    return;

                case RenderSteps.World_AlwaysFront:
                    this.Tiles.EndWorldDraw();
                    return;

                default:
                    return;
            }

            // leaves, petals and flowers go right after the game draws the ground, so they're under shadows, cliffs, buildings, crops and characters
            try
            {
                GameLocation? location = Game1.currentLocation;
                if (location is null || this.GetDrawBlocker(location, inMapScreenshots: true, weatherEffect: true) != null)
                {
                    this.Ground.Hide();
                    return;
                }

                bool enabled = this.Config.EnableMapChanges || this.PreviewActive;
                if (this.PreviewActive)
                    this.Ground.Draw(e.SpriteBatch, this.GroundTexture, this.ParticleTexture, this.PreviewInfo, this.Config, location, enabled);
                else
                    this.Ground.Draw(e.SpriteBatch, this.GroundTexture, this.ParticleTexture, this.Today, this.Config, location, enabled);
            }
            catch (Exception ex)
            {
                if (!this.LoggedGroundError)
                {
                    this.LoggedGroundError = true;
                    this.Monitor.Log($"Error while drawing ground cover (repeats are hidden until tomorrow). Technical details:\n{ex}", LogLevel.Error);
                }
            }
        }

        /// <inheritdoc cref="IDisplayEvents.RenderingStep"/>
        private void OnRenderingStep(object? sender, RenderingStepEventArgs e)
        {
            switch (e.Step)
            {
                // the game is about to draw the ground: decide what the map shows this frame
                case RenderSteps.World_Background:
                    try
                    {
                        GameLocation? location = Game1.currentLocation;
                        if (location is null || this.GetDrawBlocker(location, inMapScreenshots: true) != null)
                            this.Tiles.BeginWorldDraw(null, SeasonTileTarget.None);
                        else
                        {
                            bool enabled = this.Config.EnableMapChanges || this.PreviewActive;
                            this.Tiles.BeginWorldDraw(location, this.GetTileTarget(location, enabled));
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Tiles.EndWorldDraw();
                        if (!this.LoggedGroundError)
                        {
                            this.LoggedGroundError = true;
                            this.Monitor.Log($"Error while preparing the map's seasonal change (repeats are hidden until tomorrow). Technical details:\n{ex}", LogLevel.Error);
                        }
                    }
                    break;

                // the sorted world (including the Front layer) and then the AlwaysFront layer
                case RenderSteps.World_Sorted:
                    this.Tiles.SetStep(WorldDrawStep.Sorted);
                    break;

                case RenderSteps.World_AlwaysFront:
                    this.Tiles.SetStep(WorldDrawStep.AlwaysFront);
                    break;
            }
        }

        /// <inheritdoc cref="IContentEvents.AssetRequested"/>
        private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
        {
            try
            {
                this.Blender.OnAssetRequested(e);
            }
            catch (Exception ex)
            {
                this.Monitor.LogOnce($"Error while preparing a seasonal tree or building texture (it keeps its normal look). Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <inheritdoc cref="IContentEvents.AssetsInvalidated"/>
        private void OnAssetsInvalidated(object? sender, AssetsInvalidatedEventArgs e)
        {
            try
            {
                this.Tiles.OnAssetsInvalidated(e.NamesWithoutLocale);
            }
            catch (Exception ex)
            {
                this.Monitor.LogOnce($"Error while noting that another mod changed a tile sheet. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /****
        ** Console commands
        ****/
        /// <summary>Handle the <c>gs_status</c> console command.</summary>
        /// <param name="command">The command name.</param>
        /// <param name="args">The command arguments.</param>
        private void OnStatusCommand(string command, string[] args)
        {
            try
            {
                if (!Context.IsWorldReady)
                {
                    this.Monitor.Log("Load a save first: gs_status shows the in-game date and weather.", LogLevel.Info);
                    return;
                }

                this.RefreshDates(force: true);

                WorldDate today = new(Game1.year, Game1.season, Game1.dayOfMonth);
                SeasonCalendar.GetTomorrow(this.Config, out WorldDate tomorrow);

                string? forecast = Context.IsMainPlayer ? Game1.weatherForTomorrow : Game1.netWorldState.Value.WeatherForTomorrow;
                string forecastText = forecast is null
                    ? "unknown"
                    : $"'{forecast}' (the TV shows '{Game1.getWeatherModificationsForDate(tomorrow, forecast)}')";
                string todayWeather = Game1.netWorldState.Value.GetWeatherForLocation(LocationContexts.DefaultId).Weather ?? "unknown";

                StringBuilder report = new();
                report.AppendLine("Gradual Seasons status");
                report.AppendLine($"  Date:       {Game1.season} {Game1.dayOfMonth}, year {Game1.year}{SeasonCalendar.DescribeMonth()} (day {Game1.stats.DaysPlayed} of this save)");
                report.AppendLine($"  Today:      {this.Today.Describe()}{this.DescribeStrength(this.Today)}");
                report.AppendLine($"  Tomorrow:   {tomorrow.Season} {tomorrow.DayOfMonth}: {this.Tomorrow.Describe()}{this.DescribeStrength(this.Tomorrow)}");
                report.AppendLine($"  Forecast:   tomorrow in the valley is {forecastText}");
                report.AppendLine($"  Mod change: {this.DescribeDecision(today)}");
                report.AppendLine($"  Weather:    today in the valley is '{todayWeather}'");
                report.AppendLine($"  Particles:  {this.DescribeParticlesHere()}");
                report.AppendLine($"  Map:        {this.DescribeMapHere()}");
                report.AppendLine($"  Ground:     {this.DescribeGroundHere()}");
                report.AppendLine($"  Trees:      {(this.Config.EnableTreeAndBuildingChanges || this.Config.EnableRoofSnow ? this.Blender.Status : "turned off in the config")} (trees and roofs follow the real date, not gs_preview)");
                report.AppendLine($"  Preview:    {(this.PreviewActive ? $"on, showing {this.PreviewSeason} {this.PreviewDay} ({this.PreviewInfo.Describe()})" : "off")}");
                report.Append(
                    $"  Config:     weather changes {OnOff(this.Config.EnableWeatherChanges)}, particles {OnOff(this.Config.EnableParticles)}, map changes {OnOff(this.Config.EnableMapChanges)}, trees and buildings {OnOff(this.Config.EnableTreeAndBuildingChanges)}, roof snow {OnOff(this.Config.EnableRoofSnow)}, "
                    + $"ramp {this.Config.RampDays} days, linger {this.Config.LingerDays} days, strength {Fmt.Num(this.Config.Strength)}, "
                    + $"max {this.Config.MaxParticles} particles, during festivals {OnOff(this.Config.ShowDuringFestivals)}, debug logging {OnOff(this.Config.DebugLogging)}"
                );

                this.Monitor.Log(report.ToString(), LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while building the status report. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <summary>Handle the <c>gs_preview</c> console command.</summary>
        /// <param name="command">The command name.</param>
        /// <param name="args">The command arguments.</param>
        private void OnPreviewCommand(string command, string[] args)
        {
            try
            {
                // gs_preview off
                if (args.Length == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    if (this.PreviewActive)
                    {
                        this.PreviewActive = false;
                        this.Monitor.Log("Preview stopped. Particles and ground cover are back to today's date (the preview ones fade or melt away).", LogLevel.Info);
                    }
                    else
                        this.Monitor.Log("No preview is running.", LogLevel.Info);
                    return;
                }

                // gs_preview <season> <day>
                if (args.Length != 2
                    || !TransitionSchedule.TryParseSeason(args[0], out Season season)
                    || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)
                    || day < 1
                    || day > TransitionSchedule.DaysPerSeason)
                {
                    this.Monitor.Log("Usage: gs_preview <season> <day>, for example 'gs_preview fall 26' (season: spring, summer, fall or winter; day: 1 to 28). Use 'gs_preview off' to stop.", LogLevel.Info);
                    return;
                }

                this.PreviewSeason = season;
                this.PreviewDay = day;
                this.PreviewInfo = TransitionSchedule.Get(season, day, this.Config.RampDays, this.Config.LingerDays);
                this.PreviewActive = true;

                StringBuilder message = new();
                message.Append($"Previewing {season} {day}: {this.PreviewInfo.Describe()}.");
                message.Append($" Particles: {ParticleLayer.DescribeEffect(this.PreviewInfo)}. Ground: {GroundLayer.DescribeEffect(this.PreviewInfo)}.");
                if (!this.PreviewInfo.IsActive)
                    message.Append(" That date isn't in a transition, so nothing extra is drawn.");
                if (this.Config.Strength <= 0f)
                    message.Append(" Strength is 0 in the config, so nothing is drawn.");
                else if (this.Config.MaxParticles <= 0)
                    message.Append(" MaxParticles is 0 in the config, so no particles are drawn.");
                if (!this.Config.EnableParticles || !this.Config.EnableMapChanges)
                    message.Append(" (Some visuals are off in the config, but the preview shows them anyway.)");
                message.Append($" Right now, map: {this.DescribeMapHere()}. Particles: {this.DescribeParticlesHere()}. Ground: {this.DescribeGroundHere()}.");
                message.Append(" Type 'gs_preview off' to stop.");

                this.Monitor.Log(message.ToString(), LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while starting the preview. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /****
        ** Config
        ****/
        /// <summary>Read <c>config.json</c>, falling back to the defaults if it can't be read.</summary>
        private ModConfig LoadConfig()
        {
            ModConfig config;
            try
            {
                config = this.Helper.ReadConfig<ModConfig>();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't read config.json, so the default settings are used for now. Fix or delete the file to change the settings. Technical details:\n{ex}", LogLevel.Warn);
                config = new ModConfig();
            }

            if (config.Clamp())
                this.Monitor.Log($"Some config.json values were out of range and are being treated as: RampDays {config.RampDays} (0-{ModConfig.MaxRampDays}), LingerDays {config.LingerDays} (0-{ModConfig.MaxLingerDays}), Strength {Fmt.Num(config.Strength)} (0-2), MaxParticles {config.MaxParticles} (0-{ModConfig.MaxParticleLimit}).", LogLevel.Warn);

            return config;
        }

        /// <summary>Add the config page to Generic Mod Config Menu, if it's installed.</summary>
        private void RegisterConfigMenu()
        {
            IGenericModConfigMenuApi? api = this.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>(GenericModConfigMenuId);
            if (api is null)
                return;

            IManifest mod = this.ModManifest;
            api.Register(mod, reset: this.ResetConfig, save: this.SaveConfig);

            api.AddParagraph(mod, () => "Seasons blend into each other: in the last days of a season the next season's weather creeps in, and the old season lingers for the first days of the new one.");

            api.AddSectionTitle(mod, () => "Weather");
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.EnableWeatherChanges,
                setValue: value => this.Config.EnableWeatherChanges = value,
                name: () => "Change the weather",
                tooltip: () => "Let transitions change tomorrow's forecast (for example rain turning to snow in late fall). In multiplayer only the host's setting matters."
            );

            api.AddSectionTitle(mod, () => "Particles");
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.EnableParticles,
                setValue: value => this.Config.EnableParticles = value,
                name: () => "Show particles",
                tooltip: () => "Draw the cosmetic snow flurries, sleet, leaves and petals."
            );
            api.AddNumberOption(
                mod,
                getValue: () => this.Config.MaxParticles,
                setValue: value => this.Config.MaxParticles = value,
                name: () => "Max particles",
                tooltip: () => "The most particles of one kind on screen at once (leaves and petals use a third of this). Lower it if the game slows down.",
                min: 0,
                max: 500,
                interval: 10
            );
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.ShowDuringFestivals,
                setValue: value => this.Config.ShowDuringFestivals = value,
                name: () => "Show during festivals",
                tooltip: () => "Also draw particles and the map changes while a festival is running."
            );

            api.AddSectionTitle(mod, () => "The map");
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.EnableMapChanges,
                setValue: value => this.Config.EnableMapChanges = value,
                name: () => "Change the map",
                tooltip: () => "The map gradually turns into the next season before it arrives: the game's own next-season ground, cliffs, pond banks, town roofs and big map trees appear in natural patches (snow settling in late fall, the thaw in late winter), ponds and rivers slowly change colour, and fallen leaves, petals and flowers appear. The old season fades out the same way early in the new one. Cosmetic only."
            );

            api.AddSectionTitle(mod, () => "Trees and buildings");
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.EnableTreeAndBuildingChanges,
                setValue: value => this.Config.EnableTreeAndBuildingChanges = value,
                name: () => "Change trees",
                tooltip: () => "Trees gradually take on the next season's look (leaves turning in late summer, leaves dropping and snow settling in late fall, buds in late winter) instead of switching on day 1. This edits their textures in memory only; turning it off restores them right away."
            );
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.EnableRoofSnow,
                setValue: value => this.Config.EnableRoofSnow = value,
                name: () => "Snow on roofs",
                tooltip: () => "Snow builds up on the farmhouse and farm building roofs through late fall, stays all winter and melts in early spring (the game gives them no winter look). Turn this off if another mod already gives your buildings a winter look. In memory only; turning it off restores them right away."
            );

            api.AddSectionTitle(mod, () => "Transition timing");
            api.AddNumberOption(
                mod,
                getValue: () => this.Config.RampDays,
                setValue: value => this.Config.RampDays = value,
                name: () => "Ramp days",
                tooltip: () => "How many days at the end of each season the next season creeps in (0 turns it off). The default 7 means days 22 to 28.",
                min: 0,
                max: ModConfig.MaxRampDays,
                interval: 1
            );
            api.AddNumberOption(
                mod,
                getValue: () => this.Config.LingerDays,
                setValue: value => this.Config.LingerDays = value,
                name: () => "Linger days",
                tooltip: () => "How many days at the start of each season the previous season lingers (0 turns it off). The default 3 means days 1 to 3.",
                min: 0,
                max: ModConfig.MaxLingerDays,
                interval: 1
            );
            api.AddNumberOption(
                mod,
                getValue: () => this.Config.Strength,
                setValue: value => this.Config.Strength = value,
                name: () => "Strength",
                tooltip: () => "Multiplies every weather chance and particle count: 0 = off, 1 = normal, 2 = double.",
                min: 0f,
                max: ModConfig.MaxStrength,
                interval: 0.05f,
                formatValue: value => value.ToString("0.00", CultureInfo.InvariantCulture) + "x"
            );

            api.AddSectionTitle(mod, () => "Troubleshooting");
            api.AddBoolOption(
                mod,
                getValue: () => this.Config.DebugLogging,
                setValue: value => this.Config.DebugLogging = value,
                name: () => "Debug logging",
                tooltip: () => "Show every weather decision in the SMAPI console (normally they only go to the log file)."
            );
        }

        /// <summary>Reset the config to its defaults (called by Generic Mod Config Menu).</summary>
        private void ResetConfig()
        {
            try
            {
                this.Config = new ModConfig();
                this.ApplyConfig();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while resetting the config. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <summary>Save the config (called by Generic Mod Config Menu).</summary>
        private void SaveConfig()
        {
            try
            {
                this.Config.Clamp();
                this.Helper.WriteConfig(this.Config);
                this.ApplyConfig();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Error while saving the config. Technical details:\n{ex}", LogLevel.Error);
            }
        }

        /// <summary>Recalculate everything that depends on the config.</summary>
        private void ApplyConfig()
        {
            if (this.PreviewActive)
                this.PreviewInfo = TransitionSchedule.Get(this.PreviewSeason, this.PreviewDay, this.Config.RampDays, this.Config.LingerDays);
            if (Context.IsWorldReady)
                this.RefreshDates(force: true);

            this.Monitor.Log("Config updated.", LogLevel.Trace);
        }

        /****
        ** Helpers
        ****/
        /// <summary>Recalculate today's and tomorrow's transition state if the date changed.</summary>
        /// <param name="force">Whether to recalculate even if the date didn't change.</param>
        private void RefreshDates(bool force)
        {
            int dateKey = (Game1.year * WorldDate.DaysPerYear + (int)Game1.season * WorldDate.DaysPerMonth + Game1.dayOfMonth) * 4 + SeasonCalendar.GetMonth();
            bool dateChanged = dateKey != this.LastDateKey;
            if (!force && !dateChanged)
                return;

            this.LastDateKey = dateKey;
            this.Today = SeasonCalendar.GetToday(this.Config);
            this.Tomorrow = SeasonCalendar.GetTomorrow(this.Config, out _);

            if (dateChanged)
                this.Monitor.Log($"Date is {Game1.season} {Game1.dayOfMonth}, year {Game1.year}{SeasonCalendar.DescribeMonth()}. Today: {this.Today.Describe()}. Tomorrow: {this.Tomorrow.Describe()}.", this.Config.DebugLogging ? LogLevel.Info : LogLevel.Trace);

            // trees and buildings follow the date too (this only reloads textures when the blend actually changes)
            this.Blender.Update(this.Config);
            this.LastSeason = Game1.season;
        }

        /// <summary>Make trees and wind debris match the season after it was changed mid-day by a console command (the game normally does this overnight).</summary>
        private void RefreshSeasonalWorld()
        {
            Utility.ForEachLocation(location =>
            {
                foreach (TerrainFeature feature in location.terrainFeatures.Values)
                {
                    switch (feature)
                    {
                        // a tree remembers its own season (normally updated overnight or when you enter the location), so update
                        // that first; resetting only the texture would reload the old season's look
                        case Tree tree:
                            tree.performPlayerEntryAction();
                            break;

                        case Grass grass:
                            grass.loadSprite();
                            break;
                    }
                }

                foreach (LargeTerrainFeature feature in location.largeTerrainFeatures)
                {
                    if (feature is Bush bush)
                        bush.setUpSourceRect();
                }
                return true;
            }, includeInteriors: false);

            if (Game1.isDebrisWeather)
                Game1.populateDebrisWeatherArray();

            this.Monitor.Log("The season changed mid-day (probably a console command), so trees and wind debris were refreshed to match it.", LogLevel.Trace);
        }

        /// <summary>Get which other season's map tiles to show in a location right now.</summary>
        /// <param name="location">The location.</param>
        /// <param name="enabled">Whether the map change is wanted at all.</param>
        private SeasonTileTarget GetTileTarget(GameLocation location, bool enabled)
        {
            if (!enabled)
                return SeasonTileTarget.None;

            VanillaWeather weather = VanillaWeather.For(location);
            return this.PreviewActive
                ? SeasonTileLayer.ComputeTarget(this.PreviewInfo, this.Config, weather)
                : SeasonTileLayer.ComputeTarget(this.Today, this.Config, weather);
        }

        /// <summary>Describe the map's seasonal change in the current location, for the console commands.</summary>
        private string DescribeMapHere()
        {
            if (!this.Config.EnableMapChanges && !this.PreviewActive)
                return "turned off in the config (EnableMapChanges)";

            GameLocation? location = Game1.currentLocation;
            string? blocker = this.GetDrawBlocker(location);
            if (location is null || blocker != null)
                return $"not drawn here because {blocker ?? "there's no location"}";

            return SeasonTileLayer.Describe(this.GetTileTarget(location, enabled: true));
        }

        /// <summary>Get why the mod's visuals can't be drawn in a location right now, or <c>null</c> if they can.</summary>
        /// <param name="location">The location to check.</param>
        /// <param name="inMapScreenshots">Whether this visual belongs in the game's map screenshots (true for things on the map itself, false for particles, which are drawn across the screen and would repeat in every part of the screenshot).</param>
        /// <param name="weatherEffect">Whether this visual is a weather effect (falling particles, and leaves, petals and flowers on the ground), which is left out where it never rains or snows.</param>
        /// <remarks>This runs every frame, so it only returns constant strings (no allocations).</remarks>
        private string? GetDrawBlocker(GameLocation? location, bool inMapScreenshots = false, bool weatherEffect = false)
        {
            if (!Context.IsWorldReady || location is null)
                return "the world isn't ready";
            if (!location.IsOutdoors)
                return "you're indoors";
            if (location is Summit)
                return "the summit has its own sky";
            if (SeasonLock.KeepsOwnSeason(location))
                return "this area keeps its own season";
            if (weatherEffect && SeasonLock.HasDryClimate(location))
                return "this area's climate never brings rain or snow";
            if (!this.Config.ShowDuringFestivals && Game1.isFestival())
                return "a festival is running (see ShowDuringFestivals)";
            if (Game1.IsFakedBlackScreen())
                return "the screen is blacked out";
            if (!inMapScreenshots && Game1.game1.takingMapScreenshot)
                return "a map screenshot is being taken";
            return null;
        }

        /// <summary>Describe what particles are showing in the current location, for the console commands.</summary>
        private string DescribeParticlesHere()
        {
            TransitionInfo shown = this.PreviewActive ? this.PreviewInfo : this.Today;
            string effect = ParticleLayer.DescribeEffect(shown);

            if (this.ParticleTexture is null)
                return "disabled (the sprites couldn't be created; see the earlier error)";
            if (!this.Config.EnableParticles && !this.PreviewActive)
                return "turned off in the config";

            GameLocation? location = Game1.currentLocation;
            string? blocker = this.GetDrawBlocker(location, weatherEffect: true);
            if (location is null || blocker != null)
                return $"{effect}; not drawn here because {blocker ?? "there's no location"}";

            VanillaWeather weather = VanillaWeather.For(location);
            ParticleTargets targets = ParticleLayer.ComputeTargets(shown, this.Config, weather);
            this.Particles.GetScreenStatus(out _, out int alive);

            StringBuilder text = new();
            text.Append($"{effect}; target here in {location.NameOrUniqueName}: {ParticleLayer.DescribeTargets(targets)} ({alive} alive right now)");
            if (weather.Snowing)
                text.Append("; vanilla snow is falling, so no extra flakes");
            if (weather.DebrisType != VanillaWeather.NoDebris)
                text.Append("; vanilla wind debris is blowing, so that kind isn't doubled");
            if (weather.GreenRain)
                text.Append("; it's Green Rain, so nothing is added");
            return text.ToString();
        }

        /// <summary>Describe the ground cover in the current location, for the console commands.</summary>
        private string DescribeGroundHere()
        {
            TransitionInfo shown = this.PreviewActive ? this.PreviewInfo : this.Today;
            string effect = GroundLayer.DescribeEffect(shown);

            if (this.GroundTexture is null && this.ParticleTexture is null)
                return "disabled (the sprites couldn't be created; see the earlier error)";
            if (!this.Config.EnableMapChanges && !this.PreviewActive)
                return "turned off in the config";

            GameLocation? location = Game1.currentLocation;
            string? blocker = this.GetDrawBlocker(location, weatherEffect: true);
            if (location is null || blocker != null)
                return $"{effect}; not drawn here because {blocker ?? "there's no location"}";

            GroundCoverTarget target = GroundLayer.ComputeTarget(shown, this.Config, VanillaWeather.For(location));
            if (target.Kind == GroundCoverKind.None)
                return $"{effect}; nothing on the ground here today";

            this.Ground.GetScreenStatus(out GroundCoverKind drawnKind, out float drawnCoverage);
            return $"{effect}; {GroundLayer.GetName(target.Kind)} on {Fmt.Percent(target.Coverage)} of the open ground here (drawn right now: {GroundLayer.GetName(drawnKind)} at {Fmt.Percent(drawnCoverage)}; it catches up over a few seconds)";
        }

        /// <summary>Describe the mod's weather decision for today, for the status command.</summary>
        /// <param name="today">Today's date.</param>
        private string DescribeDecision(WorldDate today)
        {
            if (!Context.IsMainPlayer)
                return "none here: only the host's game changes the weather (you're a farmhand)";

            NudgeRecord? decision = this.Nudger.LastDecision;
            if (decision is null || decision.DecidedOnTotalDays != today.TotalDays)
                return "no decision for this date yet (the mod decides when a day starts, so sleep to trigger it)";

            return decision.Changed
                ? $"yes, {decision.TomorrowDate} changed from '{decision.OriginalForecast}' to '{decision.FinalForecast}'.\n              {decision.Summary}"
                : $"no change for {decision.TomorrowDate}.\n              {decision.Summary}";
        }

        /// <summary>Describe how the strength setting affects an intensity, like " (x1.50 strength = 1.08)".</summary>
        /// <param name="info">The transition state.</param>
        private string DescribeStrength(in TransitionInfo info)
        {
            if (!info.IsActive)
                return string.Empty;

            return $" (x{Fmt.Num(this.Config.Strength)} strength = {Fmt.Num(info.Intensity * this.Config.Strength)})";
        }

        /// <summary>Format a boolean setting.</summary>
        private static string OnOff(bool value)
        {
            return value ? "on" : "off";
        }
    }
}
