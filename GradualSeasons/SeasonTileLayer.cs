using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using xTile;
using xTile.Layers;
using xTile.ObjectModel;
using xTile.Tiles;

namespace GradualSeasons
{
    /// <summary>Which other season's map tiles to show, and how much.</summary>
    internal readonly struct SeasonTileTarget
    {
        /// <summary>Nothing to show.</summary>
        public static readonly SeasonTileTarget None = default;

        /// <summary>Whether anything is shown.</summary>
        public bool Active { get; }

        /// <summary>The season whose tiles are shown on top of the current map.</summary>
        public Season OverlaySeason { get; }

        /// <summary>Whether this is the end of a season (the next season's tiles appear) rather than the start (the previous season's tiles fade).</summary>
        public bool Ramp { get; }

        /// <summary>How much of the map has turned to the next season, from 0 to 1. The same spots turn first, across the season change.</summary>
        public float NextShare { get; }

        /// <summary>How much later (positive) or earlier (negative) stone paths change than the ground around them.</summary>
        public float PathBias { get; }

        /// <summary>Whether to scatter single specks of the overlay (a light dusting of frost or snow).</summary>
        public bool Speckles { get; }

        /// <summary>How much of the map shows the overlay season, from 0 to 1.</summary>
        public float OverlayShare => this.Ramp ? this.NextShare : 1f - this.NextShare;

        /// <summary>Construct an instance.</summary>
        public SeasonTileTarget(Season overlaySeason, bool ramp, float nextShare, float pathBias, bool speckles)
        {
            this.Active = true;
            this.OverlaySeason = overlaySeason;
            this.Ramp = ramp;
            this.NextShare = nextShare;
            this.PathBias = pathBias;
            this.Speckles = speckles;
        }
    }

    /// <summary>The part of the game's world drawing that's running for the current screen.</summary>
    internal enum WorldDrawStep
    {
        /// <summary>Not drawing the world, or there's nothing to show.</summary>
        None,

        /// <summary>The ground layers (Back, Back2...). The game sorts these by texture and starts a new batch for each one.</summary>
        Background,

        /// <summary>The Buildings layers, drawn in order.</summary>
        Buildings,

        /// <summary>The sorted world, including the Front layers (sorted by depth).</summary>
        Sorted,

        /// <summary>The AlwaysFront layers. The game sorts these by texture and starts a new batch for each one.</summary>
        AlwaysFront
    }

    /// <summary>Gradually turns the map into the next season by showing the game's own tiles for that season in natural patches.</summary>
    /// <remarks>
    /// Stardew Valley has a version of every outdoor tile sheet for each season, and swaps them on day 1. During a transition this
    /// draws the other season's version of each map tile right after the game draws that tile's layer, but only where a smooth noise
    /// pattern says that spot has turned. The pattern is worked out per pixel across the whole map and shared by every layer, so the
    /// patches have natural edges that don't follow the tile grid, cliffs and banks turn together with the ground around them, and the
    /// same spots keep their new look as the patches grow. Water is the exception: the whole pond or river slowly takes on its next-season
    /// colour together, under the game's moving water. Because these are the game's own tiles, by the end of the season the map looks
    /// almost exactly like the next season's map, so day 1 is a small step instead of a jump. The game's textures aren't changed; this
    /// only draws on top, so it's cosmetic and local.
    /// </remarks>
    internal sealed class SeasonTileLayer
    {
        /*********
        ** Fields
        *********/
        /// <summary>The size of one map tile in its tile sheet, in pixels.</summary>
        private const int TileSize = 16;

        /// <summary>The width of a tile's mask, which has a one-pixel border so patch edges can be found.</summary>
        private const int MaskSpan = TileSize + 2;

        /// <summary>The atlas width and height in pixels.</summary>
        private const int AtlasSize = 2048;

        /// <summary>The number of cells in one atlas row.</summary>
        private const int CellsPerRow = AtlasSize / TileSize;

        /// <summary>The number of cells one atlas page can hold.</summary>
        private const int AtlasCapacity = CellsPerRow * CellsPerRow;

        /// <summary>The most atlas pages a screen uses. One page is plenty for a normal view; more are only made when a lot of the map is drawn at once (zoomed far out on a big screen, or the game's whole-map screenshots), so no part of it is left unchanged for lack of room.</summary>
        private const int MaxAtlasPages = 4;

        /// <summary>The number of cells all atlas pages can hold.</summary>
        private const int TotalAtlasCapacity = AtlasCapacity * MaxAtlasPages;

        /// <summary>The largest total difference across a pixel's colour channels (0 to 1020) for a stone path pixel's change to count as the paving's colour shifting (faded evenly, so there are no stains). A bigger change, like snow, frost or grass covering it, comes and goes in patches like the ground around it. Measured on the game's own paving: between seasons without snow 94-97% of its pixels change by less than this, while snow changes them by 75-400.</summary>
        private const int PavingShiftTolerance = 75;

        /// <summary>How much darker the edge of a patch is drawn, so it reads as lying on top of the ground.</summary>
        private const float EdgeShade = 0.84f;

        /// <summary>The largest total difference across a pixel's colour channels (0 to 1020) for the two seasons to count as looking the same there.</summary>
        private const int SameColorTolerance = 24;

        /// <summary>How far stone paths follow the ground's change when snow comes and goes (they're kept clear, so they stay a bit behind).</summary>
        private const float PathSnowShare = 0.8f;

        /// <summary>The share of a tile's pixels that must look like snow for it to count as snow-covered (see <see cref="IsMostlySnow"/>).</summary>
        private const float MostlySnowShare = 0.35f;

        /// <summary>The share of a tile's pixels that must be another mod's art for this season only (see <see cref="KeptPixels"/>) for the whole tile to stay as it is.</summary>
        internal const float MostlyKeptShare = 0.5f;

        /// <summary>The share of the pixels a tile loses in the other season that must look like snow for it to melt away in patches (see <see cref="TakesAwaySnow"/>).</summary>
        private const float MeltingSnowShare = 0.5f;

        /// <summary>The share of a map's seasonal ground tiles that must be mostly another mod's art for this season only for the whole map to wait for day 1 (like a recolour that only covers the current season: changing the rest of the ground around it would look patchy).</summary>
        private const float MapKeptShare = 0.05f;

        /// <summary>How far above a Front tile its overlay is sorted, so it's drawn right on top of that tile and nothing else.</summary>
        private const float FrontDepthOffset = 0.000001f;

        /// <summary>Converts a share of the map (0 to 1) into a noise threshold, so a share of 0.3 covers about 30% of it.</summary>
        private static readonly QuantileTable FieldQuantiles = new((x, y) => Field(0x5EA5u, x, y));

        /// <summary>A value stored for each map layer the overlay listens to.</summary>
        private static readonly object Attached = new();

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The other season's tile sheets, by asset name (pixels are read once, then reused).</summary>
        private readonly Dictionary<string, SheetData?> Sheets = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The pixels of each tile sheet that another mod changed for this season only, by this season's and the other season's asset names (found once per day).</summary>
        private readonly Dictionary<string, KeptPixels?> Kept = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether each location's map waits for day 1 today (see <see cref="MapKeptShare"/>), by location name.</summary>
        private readonly Dictionary<string, bool> WaitingMaps = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The state for each split-screen player.</summary>
        private readonly PerScreen<ScreenState> Screens = new(() => new ScreenState());

        /// <summary>The map layers the overlay listens to (weakly held, so old maps can still be unloaded).</summary>
        private readonly ConditionalWeakTable<Layer, object> AttachedLayers = new();

        /// <summary>The handler added to each map layer's <c>AfterDraw</c> event.</summary>
        private readonly LayerEventHandler AfterDrawHandler;

        /// <summary>Incremented to make every screen reset on its next frame.</summary>
        private int Generation;

        /// <summary>The date the cached tile sheet pixels were read on.</summary>
        private int SheetsDateKey = -1;

        /// <summary>Whether a drawing error was already logged today (so a broken frame doesn't flood the log).</summary>
        private bool LoggedError;

        /// <summary>Whether the game's sprite batch couldn't be restarted for the texture-sorted layers, so those overlays are skipped.</summary>
        private bool CantRestartBatch;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public SeasonTileLayer(IMonitor monitor)
        {
            this.Monitor = monitor;
            this.AfterDrawHandler = this.OnLayerAfterDraw;
        }

        /// <summary>Reset every screen and forget all cached pixels (e.g. when returning to the title screen).</summary>
        public void ResetAll()
        {
            this.Generation++;
            this.ClearSheets();
            VanillaArt.ClearCache();
            this.SheetsDateKey = -1;
        }

        /// <summary>Forget the tile sheets another mod just changed, so the overlay is rebuilt from their new art. Call this from <c>Content.AssetsInvalidated</c>.</summary>
        /// <param name="names">The asset names that were reloaded.</param>
        /// <remarks>
        /// For example, Content Patcher moves a <c>{{season}}</c> edit to the new season's tile sheet as the day starts, and some
        /// edits change with the time of day or the weather. The pixels read earlier would no longer match the map.
        /// </remarks>
        public void OnAssetsInvalidated(IEnumerable<IAssetName> names)
        {
            if (this.Sheets.Count == 0)
                return;

            foreach (IAssetName name in names)
            {
                foreach (string key in this.Sheets.Keys)
                {
                    if (name.IsEquivalentTo(key))
                    {
                        this.ClearSheets();
                        this.Generation++; // every screen rebuilds its tiles
                        return;
                    }
                }
            }
        }

        /// <summary>Allow the next drawing error to be logged (called each morning).</summary>
        public void ResetErrorLog()
        {
            this.LoggedError = false;
        }

        /// <summary>Work out which season's tiles to show and how much.</summary>
        /// <param name="info">The transition state to show.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="weather">The vanilla weather right now.</param>
        public static SeasonTileTarget ComputeTarget(in TransitionInfo info, ModConfig config, in VanillaWeather weather)
        {
            if (!info.IsActive || config.Strength <= 0f)
                return SeasonTileTarget.None;

            // how far the map has turned (rises through the whole window, so there's no jump on day 1)
            bool ramp = info.Phase == TransitionPhase.Ramp;
            float next = TransitionSchedule.GetBlendShare(info, config.RampDays, config.LingerDays);
            float nextShare = ramp
                ? next * config.Strength
                : 1f - (1f - next) * config.Strength;

            // snow lies thicker after a snow day and melts a bit in the rain
            bool snowArriving = ramp && info.ToSeason == Season.Winter;
            bool snowLeaving = !ramp && info.FromSeason == Season.Winter;
            if (snowArriving)
            {
                if (weather.Snowing)
                    nextShare += 0.08f;
                else if (weather.Raining)
                    nextShare -= 0.05f;
            }
            else if (snowLeaving)
            {
                if (weather.Raining)
                    nextShare += 0.05f;
                else if (weather.Snowing)
                    nextShare -= 0.08f;
            }
            nextShare = Math.Clamp(nextShare, 0f, 1f);

            Season overlay = ramp ? info.ToSeason : info.FromSeason;
            float overlayShare = ramp ? nextShare : 1f - nextShare;
            if (overlayShare <= 0.001f)
                return SeasonTileTarget.None;

            // paths stay clear of snow longer, and lose it first
            float pathBias = snowArriving ? 0.08f : snowLeaving ? -0.08f : 0f;

            return new SeasonTileTarget(overlay, ramp, nextShare, pathBias, speckles: overlay == Season.Winter);
        }

        /// <summary>Describe a transition's map change, for the console commands.</summary>
        /// <param name="target">The target to describe.</param>
        public static string Describe(in SeasonTileTarget target)
        {
            if (!target.Active)
                return "normal (no transition)";

            string what = target.OverlaySeason == Season.Winter
                ? (target.Ramp ? "snow settling" : "the last snow melting")
                : $"{target.OverlaySeason} ground showing through";
            return $"{what}: about {Fmt.Percent(target.OverlayShare)} of the map shows its {target.OverlaySeason} look";
        }

        /// <summary>Get ready to draw the overlay for this frame. Call this as the game starts drawing the ground (<c>RenderingStep</c> for <c>RenderSteps.World_Background</c>).</summary>
        /// <param name="location">The location being drawn, or <c>null</c> to draw nothing this frame.</param>
        /// <param name="target">What to show.</param>
        public void BeginWorldDraw(GameLocation? location, in SeasonTileTarget target)
        {
            ScreenState state = this.GetState();
            state.Step = WorldDrawStep.None;
            state.FrameLocation = null;

            Map? map = location?.Map;
            if (location is null || map is null || !target.Active)
                return;

            // start over when the location, map, date or amount changes
            int dateKey = Game1.year * WorldDate.DaysPerYear + (int)Game1.season * WorldDate.DaysPerMonth + Game1.dayOfMonth;
            if (dateKey != this.SheetsDateKey)
            {
                this.ClearSheets(); // other mods may edit tile sheets daily
                VanillaArt.ClearCache();
                this.SheetsDateKey = dateKey;
            }
            if (!state.IsFor(location, map, target, dateKey))
                state.StartFor(location, map, target, dateKey);

            // a map whose ground mostly uses another mod's art for this season only (like a recolour that doesn't cover the
            // other season yet) changes on day 1 as usual, so the two looks don't mix
            if (this.IsWaitingForDayOne(location, map, target))
                return;

            // make room for this frame's new tiles now: the atlas can't be cleared once tiles that use it are waiting to be drawn
            // (when zoomed far out, just clear it if it actually ran out of room last frame)
            int room = (Game1.viewport.Width / Game1.tileSize + 2) * (Game1.viewport.Height / Game1.tileSize + 2) * 2;
            if (state.AtlasFull || (room < TotalAtlasCapacity / 2 && state.NextSlot + room > TotalAtlasCapacity))
                state.ClearCells();

            // listen to the map's layers, so each overlay is drawn right after its own layer
            if (!ReferenceEquals(state.AttachedMap, map) || state.AttachedLayerCount != map.Layers.Count)
            {
                this.Attach(map);
                state.AttachedMap = map;
                state.AttachedLayerCount = map.Layers.Count;
            }

            state.FrameLocation = location;
            state.FrameTarget = target;
            state.Step = WorldDrawStep.Background;
        }

        /// <summary>Note which part of the world the game is drawing now (see <see cref="WorldDrawStep"/>).</summary>
        /// <param name="step">The part being drawn.</param>
        public void SetStep(WorldDrawStep step)
        {
            ScreenState state = this.GetState();
            state.Step = state.FrameLocation is null ? WorldDrawStep.None : step;
        }

        /// <summary>Stop drawing until the next frame. Call this when the game is done drawing the world.</summary>
        public void EndWorldDraw()
        {
            ScreenState state = this.GetState();
            state.Step = WorldDrawStep.None;
            state.FrameLocation = null;
        }

        /// <summary>Get how much of an area is covered for a given share, for tests.</summary>
        /// <param name="share">The share of the map to cover.</param>
        /// <param name="seed">The noise seed.</param>
        internal static float MeasureCoverage(float share, uint seed)
        {
            float threshold = FieldQuantiles.Get(share);
            int covered = 0;
            int total = 0;
            for (int y = 0; y < 2400; y += 9)
            {
                for (int x = 0; x < 2400; x += 9)
                {
                    total++;
                    if (Field(seed, x, y) < threshold)
                        covered++;
                }
            }

            return covered / (float)total;
        }

        /// <summary>Get the noise threshold for a share of the map, for tests and previews.</summary>
        /// <param name="share">The share of the map that has turned, from 0 to 1.</param>
        internal static float GetThreshold(float share)
        {
            return FieldQuantiles.Get(share);
        }

        /// <summary>Get how often a single speck of the overlay is scattered outside the patches.</summary>
        /// <param name="target">What's shown.</param>
        internal static float GetSpeckleRate(in SeasonTileTarget target)
        {
            return target.Speckles ? 0.05f * Math.Min(1f, target.OverlayShare * 4f) : 0f;
        }

        /// <summary>Build the pixels for one partly turned map tile.</summary>
        /// <param name="source">The other season's tile sheet pixels.</param>
        /// <param name="sourceWidth">The tile sheet's width in pixels.</param>
        /// <param name="sourceX">The tile's X pixel position in the tile sheet.</param>
        /// <param name="sourceY">The tile's Y pixel position in the tile sheet.</param>
        /// <param name="current">The current season's tile sheet pixels (the same layout as <paramref name="source"/>), or <c>null</c> if unknown. Pixels that look the same in both seasons are left out, so a patch edge isn't drawn as a dark line across ground that doesn't change.</param>
        /// <param name="keep">Which tile sheet pixels to leave as they are (another mod's art for this season only, see <see cref="KeptPixels"/>), or <c>null</c> for none.</param>
        /// <param name="orientation">How the game draws the tile flipped or turned (see <see cref="TryGetOrientation"/>); the output is built the way round it appears on the map.</param>
        /// <param name="tileX">The tile's X position on the map.</param>
        /// <param name="tileY">The tile's Y position on the map.</param>
        /// <param name="seed">The location's noise seed.</param>
        /// <param name="threshold">The noise threshold for the current share (see <see cref="GetThreshold"/>).</param>
        /// <param name="ramp">Whether the next season is arriving (rather than the previous one fading).</param>
        /// <param name="speckleRate">How often single specks are scattered (see <see cref="GetSpeckleRate"/>).</param>
        /// <param name="pathBias">How much later each of the 3x3 tiles around this one turns (row by row), or <c>null</c> for none.</param>
        /// <param name="mask">A buffer for the mask, <see cref="MaskSpan"/> squared.</param>
        /// <param name="shadeEdges">Whether to draw the edge of each patch a little darker, so it reads as lying on top of the ground. That suits snow; for other changes the darker edge only looks like a dark line.</param>
        /// <param name="output">Receives the tile's pixels; pixels that don't show a change are transparent.</param>
        /// <param name="anyEdge">Whether any pixel was shaded as a patch edge.</param>
        /// <param name="covered">The number of pixels inside the patches (including ones that look the same in both seasons).</param>
        /// <param name="below">What the layers underneath show in the patches (see <see cref="ComposeLayersBelow"/>), drawn where this layer is more see-through in the other season so what it loses disappears too, or <c>null</c> to only draw the other season's pixels.</param>
        /// <returns>The number of pixels that visibly change.</returns>
        /// <remarks>Every map layer uses the same pattern at the same spot, so a cliff, bank or wall turns together with the ground around it.</remarks>
        internal static int ComposeTile(Color[] source, int sourceWidth, int sourceX, int sourceY, Color[]? current, bool[]? keep, int orientation, int tileX, int tileY, uint seed, float threshold, bool ramp, float speckleRate, float[]? pathBias, bool[] mask, bool shadeEdges, Color[] output, out bool anyEdge, out int covered, Color[]? below = null)
        {
            // which pixels show the overlay (with a one-pixel border, so the patch edge can be shaded)
            for (int row = -1; row <= TileSize; row++)
            {
                for (int column = -1; column <= TileSize; column++)
                {
                    int pixelX = tileX * TileSize + column;
                    int pixelY = tileY * TileSize + row;
                    float value = Field(seed, pixelX, pixelY);
                    if (pathBias != null)
                        value += Bilinear(pathBias, column, row);
                    bool turned = value < threshold;
                    bool show = ramp ? turned : !turned;
                    if (!show && speckleRate > 0f)
                        show = GroundLayer.ToUnit(GroundLayer.Hash(seed, pixelX, pixelY, 31)) < speckleRate;
                    mask[(row + 1) * MaskSpan + column + 1] = show;
                }
            }

            // compose the tile
            int shown = 0;
            covered = 0;
            anyEdge = false;
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    int maskIndex = (row + 1) * MaskSpan + column + 1;
                    if (!mask[maskIndex])
                    {
                        output[row * TileSize + column] = Color.Transparent;
                        continue;
                    }

                    covered++;
                    int spriteColumn = column;
                    int spriteRow = row;
                    if (orientation != 0)
                        GetSpritePixel(orientation, column, row, out spriteColumn, out spriteRow);
                    int sourceIndex = (sourceY + spriteRow) * sourceWidth + sourceX + spriteColumn;
                    Color color = source[sourceIndex];

                    // another mod's art for this season only stays until the season changes (the other season doesn't have it yet)
                    if (keep != null && sourceIndex < keep.Length && keep[sourceIndex])
                    {
                        output[row * TileSize + column] = Color.Transparent;
                        continue;
                    }

                    // nothing to show where both seasons look the same (an edge there would just be a dark line)
                    if (current != null && sourceIndex < current.Length && LooksSame(color, current[sourceIndex]))
                    {
                        output[row * TileSize + column] = Color.Transparent;
                        continue;
                    }

                    // where this layer has less in the other season (like snow that melts away), what's underneath shows through
                    if (below != null && current != null && sourceIndex < current.Length && ShowsLess(color, current[sourceIndex]))
                        color = Over(color, below[row * TileSize + column]);

                    shown++;
                    bool up = mask[maskIndex - MaskSpan];
                    bool down = mask[maskIndex + MaskSpan];
                    bool left = mask[maskIndex - 1];
                    bool right = mask[maskIndex + 1];
                    bool isolated = !up && !down && !left && !right;
                    if (shadeEdges && !isolated && !(up && down && left && right) && color.A > 0)
                    {
                        color = new Color((byte)(color.R * EdgeShade), (byte)(color.G * EdgeShade), (byte)(color.B * EdgeShade), color.A);
                        anyEdge = true;
                    }
                    output[row * TileSize + column] = color;
                }
            }

            return shown;
        }

        /// <summary>On a stone path tile, fade the pixels where only the paving's colour shifts between the seasons evenly toward the other season (over whatever <see cref="ComposeTile"/> drew there), so the paving doesn't change in stain-like patches.</summary>
        /// <param name="source">The other season's tile sheet pixels.</param>
        /// <param name="sourceWidth">The tile sheet's width in pixels.</param>
        /// <param name="sourceX">The tile's X pixel position in the tile sheet.</param>
        /// <param name="sourceY">The tile's Y pixel position in the tile sheet.</param>
        /// <param name="current">The current season's tile sheet pixels (the same layout).</param>
        /// <param name="keep">Which tile sheet pixels to leave as they are (see <see cref="KeptPixels"/>), or <c>null</c> for none.</param>
        /// <param name="orientation">How the game draws the tile flipped or turned (see <see cref="TryGetOrientation"/>).</param>
        /// <param name="share">How far the paving has faded, from 0 to 1.</param>
        /// <param name="output">The tile's pixels from <see cref="ComposeTile"/>, updated in place.</param>
        /// <returns>The number of pixels faded.</returns>
        /// <remarks>Pixels that change a lot (snow, frost, or grass growing over the paving) keep the patches from <see cref="ComposeTile"/>, so they follow the ground around the path instead of making the whole tile stand out as a square.</remarks>
        internal static int FadePavingShift(Color[] source, int sourceWidth, int sourceX, int sourceY, Color[] current, bool[]? keep, int orientation, float share, Color[] output)
        {
            int faded = 0;
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    int spriteColumn = column;
                    int spriteRow = row;
                    if (orientation != 0)
                        GetSpritePixel(orientation, column, row, out spriteColumn, out spriteRow);
                    int index = (sourceY + spriteRow) * sourceWidth + sourceX + spriteColumn;
                    if (index >= current.Length || (keep != null && index < keep.Length && keep[index]))
                        continue;

                    Color other = source[index];
                    if (!IsPavingShift(other, current[index]))
                        continue; // the same in both seasons, or a big change that follows the patches

                    output[row * TileSize + column] = new Color((byte)(other.R * share), (byte)(other.G * share), (byte)(other.B * share), (byte)(other.A * share));
                    faded++;
                }
            }
            return faded;
        }

        /// <summary>Get whether a tile is mostly covered in snow (like the winter version of a snowy path).</summary>
        /// <param name="pixels">The tile sheet pixels.</param>
        /// <param name="width">The tile sheet's width in pixels.</param>
        /// <param name="x">The tile's X pixel position in the tile sheet.</param>
        /// <param name="y">The tile's Y pixel position in the tile sheet.</param>
        internal static bool IsMostlySnow(Color[] pixels, int width, int x, int y)
        {
            int solid = 0;
            int snow = 0;
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    int index = (y + row) * width + x + column;
                    if (index < 0 || index >= pixels.Length || pixels[index].A < 128)
                        continue;
                    solid++;
                    if (RoofSnow.IsSnowColor(pixels[index]))
                        snow++;
                }
            }
            return solid > 0 && snow >= solid * MostlySnowShare;
        }

        /// <summary>Get whether two tile pixels look the same (allowing for tiny differences, like a recolour's dithering).</summary>
        /// <param name="a">One pixel.</param>
        /// <param name="b">The other pixel.</param>
        internal static bool LooksSame(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) + Math.Abs(a.A - b.A) <= SameColorTolerance;
        }

        /// <summary>Get how SMAPI draws a map tile flipped or turned, from its <c>@Flip</c> and <c>@Rotation</c> properties.</summary>
        /// <param name="tile">The map tile.</param>
        /// <param name="orientation">The flip in the lowest two bits (like <see cref="SpriteEffects"/>), and the number of clockwise quarter turns in the next two.</param>
        /// <returns>Whether the tile can be matched pixel for pixel (false if it's turned by an angle that isn't a quarter turn).</returns>
        /// <remarks>
        /// Tiles from .tmx maps (most mod maps and map edits) always have both properties, usually set to 0, so their value matters, not
        /// whether they're there. This reads them the same way as SMAPI's map renderer (<c>SDisplayDevice</c>).
        /// </remarks>
        internal static bool TryGetOrientation(Tile tile, out int orientation)
        {
            orientation = 0;

            if (tile.Properties.TryGetValue("@Flip", out PropertyValue? flipValue) && int.TryParse(flipValue?.ToString(), out int flip))
                orientation = flip & 3; // SpriteEffects.FlipHorizontally | FlipVertically

            if (tile.Properties.TryGetValue("@Rotation", out PropertyValue? rotationValue) && int.TryParse(rotationValue?.ToString(), out int degrees))
            {
                degrees = (degrees % 360 + 360) % 360;
                if (degrees % 90 != 0)
                    return false;
                orientation |= degrees / 90 << 2;
            }

            return true;
        }

        /// <summary>Get which pixel of a tile's sprite the game draws at a pixel of the map tile, for a tile drawn flipped or turned.</summary>
        /// <param name="orientation">How the tile is drawn (see <see cref="TryGetOrientation"/>).</param>
        /// <param name="column">The pixel's column on the map tile.</param>
        /// <param name="row">The pixel's row on the map tile.</param>
        /// <param name="spriteColumn">The column of the sprite pixel drawn there.</param>
        /// <param name="spriteRow">The row of the sprite pixel drawn there.</param>
        /// <remarks>SMAPI flips the sprite, then turns it clockwise around the tile's centre.</remarks>
        internal static void GetSpritePixel(int orientation, int column, int row, out int spriteColumn, out int spriteRow)
        {
            const int Last = TileSize - 1;

            // undo the turn
            int x, y;
            switch (orientation >> 2 & 3)
            {
                case 1: // 90°
                    x = row;
                    y = Last - column;
                    break;

                case 2: // 180°
                    x = Last - column;
                    y = Last - row;
                    break;

                case 3: // 270°
                    x = Last - row;
                    y = column;
                    break;

                default:
                    x = column;
                    y = row;
                    break;
            }

            // then the flip
            spriteColumn = (orientation & 1) != 0 ? Last - x : x;
            spriteRow = (orientation & 2) != 0 ? Last - y : y;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Get the current screen's state, resetting it if needed.</summary>
        private ScreenState GetState()
        {
            ScreenState state = this.Screens.Value;
            if (state.Generation != this.Generation)
            {
                state.Generation = this.Generation;
                state.Forget();
            }
            return state;
        }

        /// <summary>Listen to every layer the game draws in a map.</summary>
        /// <param name="map">The map.</param>
        private void Attach(Map map)
        {
            for (int i = 0; i < map.Layers.Count; i++)
            {
                Layer layer = map.Layers[i];
                if (this.AttachedLayers.TryGetValue(layer, out _))
                    continue;

                layer.AfterDraw += this.AfterDrawHandler;
                this.AttachedLayers.Add(layer, Attached);
            }
        }

        /// <summary>Draw a layer's overlay right after the game draws that layer.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="args">The event data.</param>
        private void OnLayerAfterDraw(object sender, LayerEventArgs args)
        {
            try
            {
                ScreenState state = this.GetState();
                GameLocation? location = state.FrameLocation;
                if (state.Step == WorldDrawStep.None || location is null)
                    return;

                // only the current location's layers, while the game is drawing that group of layers
                Layer layer = args.Layer;
                if (!ReferenceEquals(layer.Map, location.Map) || !FindLayer(location, layer, out WorldDrawStep group, out float sortOffset) || group != state.Step)
                    return;

                SpriteBatch batch = Game1.spriteBatch;
                if (group is WorldDrawStep.Background or WorldDrawStep.AlwaysFront)
                {
                    // the game sorts these layers by texture, which could put the overlay under its tiles, so draw it in order instead
                    if (this.CantRestartBatch)
                        return;
                    try
                    {
                        batch.End();
                    }
                    catch (InvalidOperationException ex)
                    {
                        this.CantRestartBatch = true;
                        this.Monitor.Log($"The map's seasonal change can't be drawn on the ground and treetop layers because another mod changed how the game draws them (cliffs, walls and roofs still change). Technical details:\n{ex}", LogLevel.Warn);
                        return;
                    }

                    batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    try
                    {
                        this.DrawLayer(batch, state, location, layer, group, sortOffset, args.Viewport);
                    }
                    finally
                    {
                        batch.End();
                        batch.Begin(SpriteSortMode.Texture, BlendState.AlphaBlend, SamplerState.PointClamp);
                    }
                }
                else
                    this.DrawLayer(batch, state, location, layer, group, sortOffset, args.Viewport);
            }
            catch (Exception ex)
            {
                if (!this.LoggedError)
                {
                    this.LoggedError = true;
                    this.Monitor.Log($"Error while drawing the map's seasonal change (repeats are hidden until tomorrow). Technical details:\n{ex}", LogLevel.Error);
                }
            }
        }

        /// <summary>Draw one map layer's overlay.</summary>
        private void DrawLayer(SpriteBatch batch, ScreenState state, GameLocation location, Layer layer, WorldDrawStep group, float sortOffset, xTile.Dimensions.Rectangle viewport)
        {
            SeasonTileTarget target = state.FrameTarget;
            int layerIndex = IndexOf(location.Map, layer);

            // visible tiles
            int minX = Math.Max(0, FloorDivide(viewport.X, Game1.tileSize));
            int minY = Math.Max(0, FloorDivide(viewport.Y, Game1.tileSize));
            int maxX = Math.Min(layer.LayerWidth - 1, FloorDivide(viewport.X + viewport.Width - 1, Game1.tileSize));
            int maxY = Math.Min(layer.LayerHeight - 1, FloorDivide(viewport.Y + viewport.Height - 1, Game1.tileSize));
            if (maxX < minX || maxY < minY)
                return;

            // first make sure every visible tile has its cell (the atlas must not change after drawing starts)
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Tile? tile = layer.Tiles[x, y];
                    if (tile is null)
                        continue;

                    CellKey key = new(layerIndex, x, y, tile.TileIndex); // animated tiles get a cell per frame
                    if (state.Cells.ContainsKey(key))
                        continue;

                    Cell cell = this.BuildCell(state, location, layer, tile, group, x, y, target);
                    if (cell.Cacheable)
                        state.Cells[key] = cell;
                }
            }
            state.UploadAtlasRow();

            // then draw them
            for (int y = minY; y <= maxY; y++)
            {
                // Front tiles are sorted with everything else, so draw just above each tile (same depth formula as xTile)
                float layerDepth = group == WorldDrawStep.Sorted
                    ? (y * Game1.tileSize + Game1.tileSize + sortOffset) / 10000f + FrontDepthOffset
                    : 0f;

                for (int x = minX; x <= maxX; x++)
                {
                    Tile? tile = layer.Tiles[x, y];
                    if (tile is null || !state.Cells.TryGetValue(new CellKey(layerIndex, x, y, tile.TileIndex), out Cell cell) || cell.Texture is null)
                        continue;

                    batch.Draw(
                        cell.Texture,
                        new Vector2(x * Game1.tileSize - viewport.X, y * Game1.tileSize - viewport.Y),
                        cell.Source,
                        cell.Tint,
                        0f,
                        Vector2.Zero,
                        Game1.pixelZoom,
                        SpriteEffects.None,
                        layerDepth
                    );
                }
            }
        }

        /// <summary>Build the overlay for one map tile (for an animated tile, its current frame).</summary>
        private Cell BuildCell(ScreenState state, GameLocation location, Layer layer, Tile tile, WorldDrawStep group, int x, int y, in SeasonTileTarget target)
        {
            // (the tile sheet's TileWidth/TileHeight aren't checked: sheets from .tmx files report 64, but the game always draws a
            // 16x16 area of the image, which GetTileImageBounds gives below)
            TileSheet? sheet = tile.TileSheet;
            if (sheet is null || string.IsNullOrEmpty(sheet.ImageSource))
                return Cell.Empty;

            // SMAPI can draw a tile flipped or turned (map edits and mod maps loaded from .tmx files mark every tile, usually as not
            // turned); the overlay is built the way round the tile appears, so those tiles change like the rest of the map
            if (!TryGetOrientation(tile, out int orientation))
                return Cell.Empty;

            // the other season's version of this tile
            if (!this.TryGetOtherSheet(sheet.ImageSource, target.OverlaySeason, out string otherPath, out SheetData? other))
                return Cell.Empty;
            xTile.Dimensions.Rectangle bounds = sheet.GetTileImageBounds(tile.TileIndex);
            if (!IsInside(bounds, other))
                return Cell.Empty;
            Rectangle source = new(bounds.X, bounds.Y, TileSize, TileSize);

            // another mod's art for this season only (like a Content Patcher edit with the {{season}} token) isn't in the other
            // season's tile sheet yet, so it stays as it is until the season changes instead of being painted over
            SheetData? current = this.LoadSheet(sheet.ImageSource);
            KeptPixels? kept = this.GetKeptPixels(sheet.ImageSource, current, otherPath, other);
            float keptShare = kept != null && current != null ? kept.GetTileShare(bounds.X, bounds.Y, current.Pixels) : 0f;
            if (keptShare >= MostlyKeptShare)
                return Cell.Empty;

            // water changes colour all at once, under the game's moving water (patches would look like stains)
            if (group == WorldDrawStep.Background && IsWater(location, x, y))
                return FadeWholeTile(state, other, source, orientation, keptShare > 0f ? kept : null, target.OverlayShare);

            // everything turns in natural patches, wherever the two seasons' tiles actually differ
            float[]? pathBias = target.PathBias != 0f && state.FillPathBias(x, y, target.PathBias)
                ? state.PathBias
                : null;
            Color[]? currentPixels = current != null && current.Width == other.Width && current.Height == other.Height ? current.Pixels : null;
            bool[]? keep = keptShare > 0f ? kept?.Pixels : null;
            bool snow = target.OverlaySeason == Season.Winter;

            // snow the other season doesn't have on this layer (like a drift piled against a tree, which is gone in spring) melts
            // away in the same patches as the ground: there the overlay shows what the layers underneath look like in the other
            // season, instead of the snow staying as a tile-shaped block until day 1
            Color[]? below = currentPixels != null
                && !IsWater(location, x, y)
                && TakesAwaySnow(other.Pixels, other.Width, bounds.X, bounds.Y, currentPixels, keep)
                && this.ComposeLayersBelow(state, location, layer, x, y, target)
                    ? state.BelowPixels
                    : null;

            int shown = ComposeTile(other.Pixels, other.Width, bounds.X, bounds.Y, currentPixels, keep, orientation, x, y, state.Seed, FieldQuantiles.Get(target.NextShare), target.Ramp, GetSpeckleRate(target), pathBias, state.Mask, shadeEdges: snow, state.CellPixels, out bool anyEdge, out int covered, below);

            // on paved stone paths and squares, the paving's own colour shift fades evenly instead (patches of slightly different
            // paving look like stains), a little behind the ground when snow comes (people keep paths clear); snow, frost or grass
            // on the paving still comes and goes in the same patches as the ground around it, so the path never shows as squares
            if (group == WorldDrawStep.Background && currentPixels != null && state.IsStonePath(x, y))
            {
                float share = target.OverlayShare * (snow ? PathSnowShare : 1f);
                int faded = FadePavingShift(other.Pixels, other.Width, bounds.X, bounds.Y, currentPixels, keep, orientation, share, state.CellPixels);
                if (shown == 0 && faded == 0)
                    return Cell.Empty;
                return AddToAtlas(state, Color.White);
            }

            if (shown == 0)
                return Cell.Empty;

            // a fully turned tile can be drawn straight from the other season's tile sheet
            if (covered == TileSize * TileSize && !anyEdge && orientation == 0 && keptShare <= 0f && below is null)
                return new Cell(other.Texture, source, Color.White);

            // otherwise draw it from the atlas
            return AddToAtlas(state, Color.White);
        }

        /// <summary>Get whether the other season's version of a tile takes away snow this season's version shows (like a snow drift that's gone in spring), so the overlay has to show what's underneath for the snow to melt away in patches.</summary>
        /// <param name="source">The other season's tile sheet pixels.</param>
        /// <param name="sourceWidth">The tile sheet's width in pixels.</param>
        /// <param name="sourceX">The tile's X pixel position in the tile sheet.</param>
        /// <param name="sourceY">The tile's Y pixel position in the tile sheet.</param>
        /// <param name="current">The current season's tile sheet pixels (the same layout).</param>
        /// <param name="keep">Which tile sheet pixels to leave as they are, or <c>null</c> for none.</param>
        /// <remarks>Only snow counts. Other things a season takes away (like the leaves on a map tree in late fall) stay until the season changes, since the overlay can only show the map underneath, not the people and objects behind them.</remarks>
        internal static bool TakesAwaySnow(Color[] source, int sourceWidth, int sourceX, int sourceY, Color[] current, bool[]? keep)
        {
            int removed = 0;
            int snow = 0;
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    int index = (sourceY + row) * sourceWidth + sourceX + column;
                    if (index >= current.Length || !ShowsLess(source[index], current[index]) || (keep != null && index < keep.Length && keep[index]))
                        continue;
                    removed++;
                    if (RoofSnow.IsSnowColor(current[index]))
                        snow++;
                }
            }
            return removed > 0 && snow >= removed * MeltingSnowShare;
        }

        /// <summary>Get whether a tile pixel is noticeably more see-through in the other season.</summary>
        /// <param name="other">The other season's pixel.</param>
        /// <param name="current">The current season's pixel.</param>
        internal static bool ShowsLess(Color other, Color current)
        {
            return current.A - other.A > SameColorTolerance;
        }

        /// <summary>Draw one pixel over another, the way the game blends them (both premultiplied).</summary>
        /// <param name="top">The pixel drawn on top.</param>
        /// <param name="under">The pixel underneath.</param>
        internal static Color Over(Color top, Color under)
        {
            if (top.A == 255 || under.A == 0)
                return top;

            float rest = 1f - top.A / 255f;
            return new Color(
                ToByte(top.R + under.R * rest),
                ToByte(top.G + under.G * rest),
                ToByte(top.B + under.B * rest),
                ToByte(top.A + under.A * rest)
            );
        }

        /// <summary>Round a colour channel to a byte.</summary>
        /// <param name="value">The channel value.</param>
        private static byte ToByte(float value)
        {
            return (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
        }

        /// <summary>Get whether a stone path pixel only shifts colour a little between the seasons (see <see cref="FadePavingShift"/>).</summary>
        /// <param name="other">The other season's pixel.</param>
        /// <param name="current">The current season's pixel.</param>
        internal static bool IsPavingShift(Color other, Color current)
        {
            int difference = Math.Abs(other.R - current.R) + Math.Abs(other.G - current.G) + Math.Abs(other.B - current.B) + Math.Abs(other.A - current.A);
            return difference > SameColorTolerance && difference < PavingShiftTolerance;
        }

        /// <summary>Compose what the layers drawn before a layer show at a map tile inside the other season's patches (the same way their own overlays draw them), into <see cref="ScreenState.BelowPixels"/>, the way round the tiles appear.</summary>
        /// <param name="state">The screen state.</param>
        /// <param name="location">The location.</param>
        /// <param name="layer">The layer whose tile is being built.</param>
        /// <param name="x">The tile's X position.</param>
        /// <param name="y">The tile's Y position.</param>
        /// <param name="target">What to show this frame.</param>
        /// <returns>Whether there's anything underneath that can be matched.</returns>
        private bool ComposeLayersBelow(ScreenState state, GameLocation location, Layer layer, int x, int y, in SeasonTileTarget target)
        {
            Color[] below = state.BelowPixels;
            Array.Clear(below);
            bool snowArrives = target.OverlaySeason == Season.Winter;
            float pavingShare = target.OverlayShare * (snowArrives ? PathSnowShare : 1f);

            foreach (Layer lower in GetLayersBelow(location, layer))
            {
                if (x >= lower.LayerWidth || y >= lower.LayerHeight)
                    continue;
                Tile? tile = lower.Tiles[x, y];
                if (tile is null)
                    continue;
                // (an animated tile underneath, like swaying flowers, shows its current frame there until the snow is gone)
                TileSheet? sheet = tile.TileSheet;
                if (sheet is null || string.IsNullOrEmpty(sheet.ImageSource) || !TryGetOrientation(tile, out int orientation))
                    return false;

                // the tile as the game draws it
                xTile.Dimensions.Rectangle bounds = sheet.GetTileImageBounds(tile.TileIndex);
                SheetData? current = this.LoadSheet(sheet.ImageSource);
                if (current is null || !IsInside(bounds, current))
                    return false;

                // and its own overlay, the same way BuildCell makes it
                SheetData? other = this.TryGetOtherSheet(sheet.ImageSource, target.OverlaySeason, out string otherPath, out SheetData? found) && IsInside(bounds, found)
                    ? found
                    : null;
                bool[]? keep = null;
                if (other != null)
                {
                    KeptPixels? kept = this.GetKeptPixels(sheet.ImageSource, current, otherPath, other);
                    float keptShare = kept?.GetTileShare(bounds.X, bounds.Y, current.Pixels) ?? 0f;
                    if (keptShare >= MostlyKeptShare)
                        other = null;
                    else if (keptShare > 0f)
                        keep = kept!.Pixels;
                }
                bool sameLayout = other != null && other.Width == current.Width && other.Height == current.Height;
                bool paving = sameLayout && IndexOf(location.backgroundLayers, lower) >= 0 && state.IsStonePath(x, y);
                bool melting = sameLayout && TakesAwaySnow(other!.Pixels, other.Width, bounds.X, bounds.Y, current.Pixels, keep);

                for (int row = 0; row < TileSize; row++)
                {
                    for (int column = 0; column < TileSize; column++)
                    {
                        GetSpritePixel(orientation, column, row, out int spriteColumn, out int spriteRow);
                        int i = row * TileSize + column;
                        Color now = current.Pixels[(bounds.Y + spriteRow) * current.Width + bounds.X + spriteColumn];
                        Color drawn = Over(now, below[i]);
                        if (other is null)
                        {
                            below[i] = drawn;
                            continue;
                        }

                        int index = (bounds.Y + spriteRow) * other.Width + bounds.X + spriteColumn;
                        Color next = other.Pixels[index];
                        if ((keep != null && index < keep.Length && keep[index]) || (sameLayout && LooksSame(next, now)))
                            below[i] = drawn;
                        else if (paving && IsPavingShift(next, now))
                            below[i] = Over(new Color((byte)(next.R * pavingShare), (byte)(next.G * pavingShare), (byte)(next.B * pavingShare), (byte)(next.A * pavingShare)), drawn);
                        else if (melting && ShowsLess(next, now))
                            below[i] = Over(next, below[i]);
                        else
                            below[i] = Over(next, drawn);
                    }
                }
            }

            foreach (Color pixel in below)
            {
                if (pixel.A > 0)
                    return true;
            }
            return false;
        }

        /// <summary>Get the map layers drawn before a layer, in the order they're drawn.</summary>
        /// <param name="location">The location.</param>
        /// <param name="layer">The layer.</param>
        private static IEnumerable<Layer> GetLayersBelow(GameLocation location, Layer layer)
        {
            foreach (List<KeyValuePair<Layer, int>> group in new[] { location.backgroundLayers, location.buildingLayers, location.frontLayers, location.alwaysFrontLayers })
            {
                foreach (KeyValuePair<Layer, int> pair in group)
                {
                    if (ReferenceEquals(pair.Key, layer))
                        yield break;
                    yield return pair.Key;
                }
            }
        }

        /// <summary>Build a tile that fades evenly to the other season's version (for water and stone paths).</summary>
        /// <param name="state">The screen state.</param>
        /// <param name="other">The other season's tile sheet.</param>
        /// <param name="source">The tile's area in the tile sheet.</param>
        /// <param name="orientation">How the game draws the tile (see <see cref="TryGetOrientation"/>).</param>
        /// <param name="kept">The tile sheet pixels to leave as they are, or <c>null</c> if the tile has none.</param>
        /// <param name="share">How far the tile has faded, from 0 to 1.</param>
        private static Cell FadeWholeTile(ScreenState state, SheetData other, Rectangle source, int orientation, KeptPixels? kept, float share)
        {
            if (orientation == 0 && kept is null)
                return new Cell(other.Texture, source, Color.White * share);

            CopyTile(other.Pixels, other.Width, source.X, source.Y, orientation, kept?.Pixels, state.CellPixels);
            return AddToAtlas(state, Color.White * share);
        }

        /// <summary>Get whether a tile's area lies inside a tile sheet image.</summary>
        /// <param name="bounds">The tile's area in the tile sheet.</param>
        /// <param name="sheet">The tile sheet image.</param>
        private static bool IsInside(xTile.Dimensions.Rectangle bounds, SheetData sheet)
        {
            return bounds.Width == TileSize && bounds.Height == TileSize && bounds.X >= 0 && bounds.Y >= 0 && bounds.X + TileSize <= sheet.Width && bounds.Y + TileSize <= sheet.Height;
        }

        /// <summary>Get whether a location's map should change on day 1 as usual instead of gradually today, because most of its ground uses another mod's art for this season only.</summary>
        /// <param name="location">The location.</param>
        /// <param name="map">Its map.</param>
        /// <param name="target">What would be shown.</param>
        /// <remarks>
        /// That happens with a recolour that only covers the current season's tile sheets (for example with Content Patcher's
        /// <c>{{season}}</c> token): the other season's tile sheets read today are still the game's own, so the recoloured ground
        /// would stay as it is while the paths, cliffs or buildings from other tile sheets turned around it. Small edits (a
        /// seasonal special order board, a dog in its doghouse) don't count; those few tiles just keep their look until day 1.
        /// </remarks>
        private bool IsWaitingForDayOne(GameLocation location, Map map, in SeasonTileTarget target)
        {
            string key = location.NameOrUniqueName ?? string.Empty;
            if (this.WaitingMaps.TryGetValue(key, out bool waiting))
                return waiting;

            int seasonal = 0;
            int kept = 0;
            string? keptSheet = null;
            Layer? back = map.GetLayer("Back");
            if (back != null)
            {
                Dictionary<TileSheet, (SheetData? Current, SheetData? Other, KeptPixels? Kept)> sheets = new();
                for (int y = 0; y < back.LayerHeight; y++)
                {
                    for (int x = 0; x < back.LayerWidth; x++)
                    {
                        Tile? tile = back.Tiles[x, y];
                        TileSheet? sheet = tile?.TileSheet;
                        if (tile is null || sheet is null || string.IsNullOrEmpty(sheet.ImageSource))
                            continue;

                        if (!sheets.TryGetValue(sheet, out var data))
                        {
                            data = (null, null, null);
                            if (this.TryGetOtherSheet(sheet.ImageSource, target.OverlaySeason, out string otherPath, out SheetData? other))
                            {
                                SheetData? current = this.LoadSheet(sheet.ImageSource);
                                data = (current, other, this.GetKeptPixels(sheet.ImageSource, current, otherPath, other));
                            }
                            sheets[sheet] = data;
                        }
                        if (data.Other is null)
                            continue;

                        xTile.Dimensions.Rectangle bounds = sheet.GetTileImageBounds(tile.TileIndex);
                        if (!IsInside(bounds, data.Other))
                            continue;

                        seasonal++;
                        if (data.Kept != null && data.Current != null && data.Kept.GetTileShare(bounds.X, bounds.Y, data.Current.Pixels) >= MostlyKeptShare)
                        {
                            kept++;
                            keptSheet ??= sheet.ImageSource;
                        }
                    }
                }
            }

            waiting = seasonal > 0 && kept >= seasonal * MapKeptShare;
            if (waiting)
            {
                this.Monitor.LogOnce("Another mod (probably a recolour) changes how some of this season's tile sheets look, but the other season's versions don't have that look today. Maps whose ground mostly uses those tile sheets change on day 1 as usual instead of gradually, so the two looks don't mix.", LogLevel.Info);
                this.Monitor.Log($"The map of {key} changes on day 1 as usual instead of gradually today: {kept} of its {seasonal} seasonal ground tiles use art another mod changed for this season only (like in {keptSheet}).", LogLevel.Trace);
            }
            this.WaitingMaps[key] = waiting;
            return waiting;
        }

        /// <summary>Add the tile in <see cref="ScreenState.CellPixels"/> to the atlas, or wait for the next frame if it's full.</summary>
        /// <param name="state">The screen state.</param>
        /// <param name="tint">The colour to draw the tile with.</param>
        private static Cell AddToAtlas(ScreenState state, Color tint)
        {
            if (state.NextSlot >= TotalAtlasCapacity)
            {
                state.AtlasFull = true;
                return Cell.NotYet;
            }
            int page = state.NextSlot / AtlasCapacity;
            while (state.Atlases.Count <= page)
                state.Atlases.Add(new Texture2D(Game1.graphics.GraphicsDevice, AtlasSize, AtlasSize));
            Rectangle area = state.AddToAtlas(state.CellPixels);
            return new Cell(state.Atlases[page], area, tint);
        }

        /// <summary>Copy a whole tile the way round the game draws it.</summary>
        /// <param name="source">The tile sheet pixels.</param>
        /// <param name="sourceWidth">The tile sheet's width in pixels.</param>
        /// <param name="sourceX">The tile's X pixel position in the tile sheet.</param>
        /// <param name="sourceY">The tile's Y pixel position in the tile sheet.</param>
        /// <param name="orientation">How the game draws the tile (see <see cref="TryGetOrientation"/>).</param>
        /// <param name="keep">Which tile sheet pixels to leave out (see <see cref="KeptPixels"/>), or <c>null</c> for none.</param>
        /// <param name="output">Receives the tile's pixels.</param>
        private static void CopyTile(Color[] source, int sourceWidth, int sourceX, int sourceY, int orientation, bool[]? keep, Color[] output)
        {
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    GetSpritePixel(orientation, column, row, out int spriteColumn, out int spriteRow);
                    int index = (sourceY + spriteRow) * sourceWidth + sourceX + spriteColumn;
                    output[row * TileSize + column] = keep != null && index < keep.Length && keep[index] ? Color.Transparent : source[index];
                }
            }
        }

        /// <summary>Get a tile sheet's version for another season, if it has one.</summary>
        /// <param name="imageSource">The tile sheet's current image source, like <c>Maps\fall_outdoorsTileSheet</c>.</param>
        /// <param name="season">The season to get.</param>
        /// <param name="path">The other season's asset name, like <c>Maps\winter_outdoorsTileSheet</c>.</param>
        /// <param name="sheet">The other season's tile sheet.</param>
        private bool TryGetOtherSheet(string imageSource, Season season, out string path, [NotNullWhen(true)] out SheetData? sheet)
        {
            path = GameLocation.GetSeasonalTilesheetName(imageSource, Utility.getSeasonKey(season));
            sheet = string.Equals(path, imageSource, StringComparison.OrdinalIgnoreCase)
                ? null // not a seasonal tile sheet
                : this.LoadSheet(path);
            return sheet != null;
        }

        /// <summary>Find which pixels of a tile sheet are another mod's art for this season only, which the other season's version doesn't have yet (found once per day).</summary>
        /// <param name="currentPath">This season's asset name.</param>
        /// <param name="current">This season's tile sheet, if it could be read.</param>
        /// <param name="otherPath">The other season's asset name.</param>
        /// <param name="other">The other season's tile sheet.</param>
        /// <returns>The pixels to keep, or <c>null</c> if there are none (or it can't be told, like for a mod's own tile sheet).</returns>
        private KeptPixels? GetKeptPixels(string currentPath, SheetData? current, string otherPath, SheetData other)
        {
            if (current is null || current.Width != other.Width || current.Height != other.Height)
                return null;

            string key = currentPath + "|" + otherPath;
            if (this.Kept.TryGetValue(key, out KeptPixels? kept))
                return kept;

            // compare with the game's own versions (a mod's own tile sheets have none, and change together)
            kept = null;
            if (this.TryGetSeasonArt(currentPath, current, out SeasonArt currentArt) && this.TryGetSeasonArt(otherPath, other, out SeasonArt otherArt))
            {
                List<SeasonArt> rest = new();
                foreach (Season season in new[] { Season.Spring, Season.Summer, Season.Fall, Season.Winter })
                {
                    string path = GameLocation.GetSeasonalTilesheetName(currentPath, Utility.getSeasonKey(season));
                    if (string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase) || string.Equals(path, otherPath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (this.LoadSheet(path) is SheetData sheet && this.TryGetSeasonArt(path, sheet, out SeasonArt art))
                        rest.Add(art);
                }

                kept = KeptPixels.Find(currentArt, otherArt, rest, mapTiles: true);
                if (kept != null)
                    this.Monitor.Log($"Another mod changed or redrew {kept.Count} pixels of {currentPath} without changing {otherPath} or the other seasons to match (it probably only applies to the current season), so those pixels keep their look until the season changes.", LogLevel.Trace);
            }

            this.Kept[key] = kept;
            return kept;
        }

        /// <summary>Get a tile sheet's pixels together with the game's own version of it.</summary>
        /// <param name="path">The tile sheet's asset name.</param>
        /// <param name="sheet">The tile sheet as the game uses it.</param>
        /// <param name="art">The tile sheet and the game's own version.</param>
        /// <returns>Whether the game has its own version (a mod's own tile sheet doesn't).</returns>
        private bool TryGetSeasonArt(string path, SheetData sheet, out SeasonArt art)
        {
            Color[]? vanilla = VanillaArt.TryLoad(path, this.Monitor, out int width, out int height);
            art = vanilla != null ? new SeasonArt(sheet.Pixels, sheet.Width, sheet.Height, vanilla, width, height) : default;
            return vanilla != null;
        }

        /// <summary>Forget the tile sheet pixels read so far and everything worked out from them.</summary>
        private void ClearSheets()
        {
            this.Sheets.Clear();
            this.Kept.Clear();
            this.WaitingMaps.Clear();
        }

        /// <summary>Get the pixels of a tile sheet image, if it exists (read once per day, then reused).</summary>
        /// <param name="path">The image's asset name, like <c>Maps\winter_outdoorsTileSheet</c>.</param>
        private SheetData? LoadSheet(string path)
        {
            if (this.Sheets.TryGetValue(path, out SheetData? data))
                return data;

            try
            {
                Texture2D texture = Game1.content.Load<Texture2D>(path);
                Color[] pixels = new Color[texture.Width * texture.Height];
                texture.GetData(pixels);
                data = new SheetData(texture, pixels, texture.Width, texture.Height);
            }
            catch
            {
                data = null; // no such image (like a season with no version of this tile sheet)
            }

            this.Sheets[path] = data;
            return data;
        }

        /// <summary>Get whether the game draws moving water on a tile.</summary>
        private static bool IsWater(GameLocation location, int x, int y)
        {
            WaterTiles.WaterTileData[,]? water = location.waterTiles?.waterTiles;
            return water != null
                && x < water.GetLength(0)
                && y < water.GetLength(1)
                && water[x, y].isWater;
        }

        /// <summary>Find which group of layers the game draws a layer with.</summary>
        /// <param name="location">The location whose map contains the layer.</param>
        /// <param name="layer">The layer.</param>
        /// <param name="group">The part of the world drawing the layer belongs to.</param>
        /// <param name="sortOffset">For a Front layer, the depth offset the game draws it with.</param>
        private static bool FindLayer(GameLocation location, Layer layer, out WorldDrawStep group, out float sortOffset)
        {
            sortOffset = 0f;
            if (IndexOf(location.backgroundLayers, layer) >= 0)
            {
                group = WorldDrawStep.Background;
                return true;
            }
            if (IndexOf(location.buildingLayers, layer) >= 0)
            {
                group = WorldDrawStep.Buildings;
                return true;
            }
            int front = IndexOf(location.frontLayers, layer);
            if (front >= 0)
            {
                // same as the game: 64 + 0.1 spread across the Front layers
                int count = location.frontLayers.Count;
                sortOffset = 64f + 0.1f * (count > 1 ? front / (float)(count - 1) : 0f);
                group = WorldDrawStep.Sorted;
                return true;
            }
            if (IndexOf(location.alwaysFrontLayers, layer) >= 0)
            {
                group = WorldDrawStep.AlwaysFront;
                return true;
            }

            group = WorldDrawStep.None;
            return false;
        }

        /// <summary>Get a layer's position in one of the game's sorted layer lists, or -1 if it's not there.</summary>
        private static int IndexOf(List<KeyValuePair<Layer, int>> layers, Layer layer)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                if (ReferenceEquals(layers[i].Key, layer))
                    return i;
            }
            return -1;
        }

        /// <summary>Get a layer's position in its map.</summary>
        private static int IndexOf(Map map, Layer layer)
        {
            for (int i = 0; i < map.Layers.Count; i++)
            {
                if (ReferenceEquals(map.Layers[i], layer))
                    return i;
            }
            return -1;
        }

        /// <summary>Divide and round down, also for negative numbers.</summary>
        private static int FloorDivide(int value, int divisor)
        {
            int result = value / divisor;
            return value % divisor != 0 && value < 0 ? result - 1 : result;
        }

        /// <summary>Interpolate a 3x3 grid of per-tile values (at tile centres) at a pixel of the middle tile, so the value has no seams at tile edges.</summary>
        /// <param name="grid">The values, row by row.</param>
        /// <param name="column">The pixel's column in the middle tile (-1 to 16).</param>
        /// <param name="row">The pixel's row in the middle tile (-1 to 16).</param>
        private static float Bilinear(float[] grid, int column, int row)
        {
            float fx = (column + 0.5f) / TileSize - 0.5f;
            float fy = (row + 0.5f) / TileSize - 0.5f;
            int left = fx < 0f ? 0 : 1;
            int top = fy < 0f ? 0 : 1;
            float tx = fx < 0f ? fx + 1f : fx;
            float ty = fy < 0f ? fy + 1f : fy;

            float topLeft = grid[top * 3 + left];
            float topRight = grid[top * 3 + left + 1];
            float bottomLeft = grid[(top + 1) * 3 + left];
            float bottomRight = grid[(top + 1) * 3 + left + 1];
            float upper = topLeft + (topRight - topLeft) * tx;
            float lower = bottomLeft + (bottomRight - bottomLeft) * tx;
            return upper + (lower - upper) * ty;
        }

        /// <summary>Get the smooth noise value for a world pixel: big soft patches, with smaller wobbles along their edges.</summary>
        /// <param name="seed">The location seed.</param>
        /// <param name="x">The world X position in game pixels (16 per tile).</param>
        /// <param name="y">The world Y position in game pixels.</param>
        private static float Field(uint seed, int x, int y)
        {
            return 0.55f * GroundLayer.ValueNoise(seed, x, y, 46f, 11)
                + 0.30f * GroundLayer.ValueNoise(seed, x, y, 15f, 12)
                + 0.15f * GroundLayer.ValueNoise(seed, x, y, 5f, 13);
        }


        /*********
        ** Private types
        *********/
        /// <summary>Identifies the overlay for one frame of one map tile.</summary>
        private readonly struct CellKey : IEquatable<CellKey>
        {
            /// <summary>The layer's position in the map.</summary>
            private readonly int Layer;

            /// <summary>The tile's X position.</summary>
            private readonly int X;

            /// <summary>The tile's Y position.</summary>
            private readonly int Y;

            /// <summary>The tile sheet index being shown (differs for each frame of an animated tile).</summary>
            private readonly int TileIndex;

            /// <summary>Construct an instance.</summary>
            public CellKey(int layer, int x, int y, int tileIndex)
            {
                this.Layer = layer;
                this.X = x;
                this.Y = y;
                this.TileIndex = tileIndex;
            }

            /// <inheritdoc />
            public bool Equals(CellKey other)
            {
                return this.Layer == other.Layer && this.X == other.X && this.Y == other.Y && this.TileIndex == other.TileIndex;
            }

            /// <inheritdoc />
            public override bool Equals(object? obj)
            {
                return obj is CellKey other && this.Equals(other);
            }

            /// <inheritdoc />
            public override int GetHashCode()
            {
                return HashCode.Combine(this.Layer, this.X, this.Y, this.TileIndex);
            }
        }

        /// <summary>The overlay for one map tile.</summary>
        private readonly struct Cell
        {
            /// <summary>Draw nothing.</summary>
            public static readonly Cell Empty = new(null, Rectangle.Empty, Color.Transparent, cacheable: true);

            /// <summary>Draw nothing this frame, and try again next frame.</summary>
            public static readonly Cell NotYet = new(null, Rectangle.Empty, Color.Transparent, cacheable: false);

            /// <summary>The texture to draw from, or <c>null</c> to draw nothing.</summary>
            public Texture2D? Texture { get; }

            /// <summary>The area of the texture to draw.</summary>
            public Rectangle Source { get; }

            /// <summary>The colour to draw it with (white, or partly transparent for water).</summary>
            public Color Tint { get; }

            /// <summary>Whether the result can be reused on later frames.</summary>
            public bool Cacheable { get; }

            /// <summary>Construct an instance.</summary>
            public Cell(Texture2D texture, Rectangle source, Color tint)
                : this(texture, source, tint, cacheable: true) { }

            /// <summary>Construct an instance.</summary>
            private Cell(Texture2D? texture, Rectangle source, Color tint, bool cacheable)
            {
                this.Texture = texture;
                this.Source = source;
                this.Tint = tint;
                this.Cacheable = cacheable;
            }
        }

        /// <summary>A tile sheet's pixels.</summary>
        private sealed class SheetData
        {
            /// <summary>The texture.</summary>
            public Texture2D Texture { get; }

            /// <summary>Its pixels, row by row.</summary>
            public Color[] Pixels { get; }

            /// <summary>Its width.</summary>
            public int Width { get; }

            /// <summary>Its height.</summary>
            public int Height { get; }

            /// <summary>Construct an instance.</summary>
            public SheetData(Texture2D texture, Color[] pixels, int width, int height)
            {
                this.Texture = texture;
                this.Pixels = pixels;
                this.Width = width;
                this.Height = height;
            }
        }

        /// <summary>The overlay state for one split-screen player.</summary>
        private sealed class ScreenState
        {
            /// <summary>The <see cref="SeasonTileLayer.Generation"/> this state was last reset for.</summary>
            public int Generation = -1;

            /// <summary>The atlas pages holding partly turned tiles (made as needed).</summary>
            public readonly List<Texture2D> Atlases = new();

            /// <summary>The next free atlas cell, counting across the pages.</summary>
            public int NextSlot;

            /// <summary>Whether the atlas ran out of room this frame (it's cleared on the next one).</summary>
            public bool AtlasFull;

            /// <summary>The pixels of the atlas row being filled, uploaded in one go instead of one tile at a time.</summary>
            private readonly Color[] RowPixels = new Color[AtlasSize * TileSize];

            /// <summary>The atlas row in <see cref="RowPixels"/>, counting across the pages, or -1 if none.</summary>
            private int Row = -1;

            /// <summary>Whether <see cref="RowPixels"/> has changes that aren't uploaded yet.</summary>
            private bool RowChanged;

            /// <summary>The overlay for each tile built so far.</summary>
            public readonly Dictionary<CellKey, Cell> Cells = new();

            /// <summary>A reusable pixel buffer for building cells.</summary>
            public readonly Color[] CellPixels = new Color[TileSize * TileSize];

            /// <summary>A reusable mask buffer for building cells.</summary>
            public readonly bool[] Mask = new bool[MaskSpan * MaskSpan];

            /// <summary>A reusable buffer for the path bias around a tile (3x3 tiles, row by row).</summary>
            public readonly float[] PathBias = new float[9];

            /// <summary>A reusable pixel buffer for what the layers under a tile show (see <see cref="ComposeLayersBelow"/>).</summary>
            public readonly Color[] BelowPixels = new Color[TileSize * TileSize];

            /// <summary>The noise seed for the current location.</summary>
            public uint Seed;

            /// <summary>The part of the world the game is drawing now.</summary>
            public WorldDrawStep Step;

            /// <summary>The location being drawn this frame, or <c>null</c> to draw nothing.</summary>
            public GameLocation? FrameLocation;

            /// <summary>What to show this frame.</summary>
            public SeasonTileTarget FrameTarget;

            /// <summary>The map whose layers were last checked for listeners.</summary>
            public Map? AttachedMap;

            /// <summary>How many layers that map had then.</summary>
            public int AttachedLayerCount;

            /// <summary>The current map's ground layer.</summary>
            private Layer? BackLayer;

            /// <summary>Whether each ground tile is a stone path (-1 not checked yet, 0 no, 1 yes), row by row.</summary>
            private sbyte[] StoneTiles = Array.Empty<sbyte>();

            /// <summary>The location the cells are for.</summary>
            private GameLocation? Location;

            /// <summary>The map the cells are for.</summary>
            private Map? Map;

            /// <summary>The date the cells are for.</summary>
            private int DateKey = -1;

            /// <summary>The season the cells show.</summary>
            private Season OverlaySeason;

            /// <summary>Whether the cells are for the end of a season.</summary>
            private bool Ramp;

            /// <summary>The next-season share the cells were built for.</summary>
            private float NextShare = -1f;

            /// <summary>Get whether the cells are still valid.</summary>
            public bool IsFor(GameLocation location, Map map, in SeasonTileTarget target, int dateKey)
            {
                return ReferenceEquals(this.Location, location)
                    && ReferenceEquals(this.Map, map)
                    && this.DateKey == dateKey
                    && this.OverlaySeason == target.OverlaySeason
                    && this.Ramp == target.Ramp
                    && Math.Abs(this.NextShare - target.NextShare) < 0.0005f;
            }

            /// <summary>Forget the current cells and start over for a new location, map, date or amount.</summary>
            public void StartFor(GameLocation location, Map map, in SeasonTileTarget target, int dateKey)
            {
                this.ClearCells();
                this.Location = location;
                this.Map = map;
                this.DateKey = dateKey;
                this.OverlaySeason = target.OverlaySeason;
                this.Ramp = target.Ramp;
                this.NextShare = target.NextShare;
                this.Seed = GroundLayer.StableHash(location.NameOrUniqueName ?? string.Empty);

                this.BackLayer = map.GetLayer("Back");
                int size = this.BackLayer is null ? 0 : this.BackLayer.LayerWidth * this.BackLayer.LayerHeight;
                if (this.StoneTiles.Length != size)
                    this.StoneTiles = new sbyte[size];
                Array.Fill(this.StoneTiles, (sbyte)-1);

                // let go of extra atlas pages a big view needed earlier (like a whole-map screenshot)
                this.TrimAtlases();
            }

            /// <summary>Dispose every atlas page after the first.</summary>
            public void TrimAtlases()
            {
                for (int i = this.Atlases.Count - 1; i >= 1; i--)
                {
                    this.Atlases[i].Dispose();
                    this.Atlases.RemoveAt(i);
                }
            }

            /// <summary>Forget the cells (the atlas is reused).</summary>
            public void ClearCells()
            {
                this.Cells.Clear();
                this.NextSlot = 0;
                this.AtlasFull = false;
                this.Row = -1;
                this.RowChanged = false;
            }

            /// <summary>Put a tile's pixels in the next free atlas cell. Call <see cref="UploadAtlasRow"/> before drawing from it.</summary>
            /// <param name="pixels">The tile's pixels, row by row.</param>
            /// <returns>The cell's area in the atlas.</returns>
            public Rectangle AddToAtlas(Color[] pixels)
            {
                int slot = this.NextSlot++;
                int row = slot / CellsPerRow; // counting across the pages
                int column = slot % CellsPerRow;
                if (row != this.Row)
                {
                    this.UploadAtlasRow();
                    this.Row = row;
                    Array.Clear(this.RowPixels);
                }

                for (int y = 0; y < TileSize; y++)
                    Array.Copy(pixels, y * TileSize, this.RowPixels, y * AtlasSize + column * TileSize, TileSize);
                this.RowChanged = true;

                return new Rectangle(column * TileSize, row % CellsPerRow * TileSize, TileSize, TileSize);
            }

            /// <summary>Upload the atlas row being filled, if it changed.</summary>
            public void UploadAtlasRow()
            {
                if (!this.RowChanged || this.Row < 0)
                    return;
                int page = this.Row / CellsPerRow;
                if (page >= this.Atlases.Count)
                    return;

                this.Atlases[page].SetData(0, new Rectangle(0, this.Row % CellsPerRow * TileSize, AtlasSize, TileSize), this.RowPixels, 0, this.RowPixels.Length);
                this.RowChanged = false;
            }

            /// <summary>Forget everything.</summary>
            public void Forget()
            {
                this.ClearCells();
                this.Step = WorldDrawStep.None;
                this.FrameLocation = null;
                this.FrameTarget = default;
                this.AttachedMap = null;
                this.AttachedLayerCount = 0;
                this.BackLayer = null;
                this.StoneTiles = Array.Empty<sbyte>();
                this.Location = null;
                this.Map = null;
                this.DateKey = -1;
                this.NextShare = -1f;
            }

            /// <summary>Fill <see cref="PathBias"/> for the 3x3 tiles around a tile.</summary>
            /// <param name="x">The middle tile's X position.</param>
            /// <param name="y">The middle tile's Y position.</param>
            /// <param name="strength">The bias on a stone path tile.</param>
            /// <returns>Whether any of those tiles is a stone path.</returns>
            public bool FillPathBias(int x, int y, float strength)
            {
                bool any = false;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        bool stone = this.IsStone(x + dx, y + dy);
                        this.PathBias[(dy + 1) * 3 + dx + 1] = stone ? strength : 0f;
                        any |= stone;
                    }
                }
                return any;
            }

            /// <summary>Get whether a ground tile is a stone path.</summary>
            public bool IsStonePath(int x, int y)
            {
                return this.IsStone(x, y);
            }

            /// <summary>Get whether a ground tile is a stone path (cached per tile).</summary>
            private bool IsStone(int x, int y)
            {
                Layer? back = this.BackLayer;
                if (back is null || x < 0 || y < 0 || x >= back.LayerWidth || y >= back.LayerHeight)
                    return false;

                int index = y * back.LayerWidth + x;
                sbyte known = this.StoneTiles[index];
                if (known >= 0)
                    return known == 1;

                Tile? tile = back.Tiles[x, y];
                bool stone = tile != null
                    && (tile.Properties.TryGetValue("Type", out PropertyValue? type) || tile.TileIndexProperties.TryGetValue("Type", out type))
                    && type?.ToString() == "Stone";
                this.StoneTiles[index] = stone ? (sbyte)1 : (sbyte)0;
                return stone;
            }
        }
    }

    /// <summary>Maps a share (0 to 1) to a noise threshold, so a share of 0.3 covers about 30% of an area.</summary>
    internal sealed class QuantileTable
    {
        /// <summary>The noise value at each percent, from 0% to 100%.</summary>
        private readonly float[] Values = new float[101];

        /// <summary>Construct an instance by sampling a noise function.</summary>
        /// <param name="sample">The noise function to sample.</param>
        public QuantileTable(Func<int, int, float> sample)
        {
            // sample a wide area (thousands of pixels across), so even the biggest patches are well represented
            const int Side = 200;
            float[] samples = new float[Side * Side];
            for (int y = 0; y < Side; y++)
            {
                for (int x = 0; x < Side; x++)
                    samples[y * Side + x] = sample(x * 23 + 3, y * 23 + 5);
            }
            Array.Sort(samples);

            for (int i = 0; i <= 100; i++)
                this.Values[i] = samples[Math.Min(samples.Length - 1, i * (samples.Length - 1) / 100)];
        }

        /// <summary>Get the noise threshold below which about this share of values fall.</summary>
        /// <param name="share">The share, from 0 to 1.</param>
        public float Get(float share)
        {
            if (share <= 0f)
                return float.NegativeInfinity;
            if (share >= 1f)
                return float.PositiveInfinity;

            float position = share * 100f;
            int index = (int)position;
            float fraction = position - index;
            return this.Values[index] + (this.Values[Math.Min(100, index + 1)] - this.Values[index]) * fraction;
        }

        /// <summary>Get the share of values that fall below a noise value (the inverse of <see cref="Get"/>).</summary>
        /// <param name="value">The noise value.</param>
        public float GetShare(float value)
        {
            if (value <= this.Values[0])
                return 0f;
            if (value >= this.Values[100])
                return 1f;

            int low = 0;
            int high = 100;
            while (high - low > 1)
            {
                int middle = (low + high) / 2;
                if (this.Values[middle] <= value)
                    low = middle;
                else
                    high = middle;
            }

            float span = this.Values[high] - this.Values[low];
            float fraction = span > 0f ? (value - this.Values[low]) / span : 0f;
            return (low + fraction) / 100f;
        }
    }
}
