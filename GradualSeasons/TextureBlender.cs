using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Buildings;
using StardewValley.GameData.WildTrees;
using StardewValley.TerrainFeatures;

namespace GradualSeasons
{
    /// <summary>Gradually changes trees and buildings to the next season's look during a transition.</summary>
    /// <remarks>
    /// The game swaps tree and farm building textures all at once on day 1 of each season. (Town buildings are map tiles, so the
    /// map overlay in <see cref="SeasonTileLayer"/> changes them together with the ground around them.) During a transition, this
    /// edits the current season's textures (through SMAPI's content API, so other mods' edits still apply) so they shift toward the
    /// other season's version. Leaf-sized clumps change in a fixed order: within a clump the colour fades smoothly from one season to
    /// the other (leaves turning, frost settling), and where the shape differs (leaves dropping, buds opening) the whole clump
    /// changes together, so there's no speckled noise. The amount keeps growing across the season change, so nothing jumps back.
    /// Farm buildings have no winter look at all, so snow settles on their roofs through late fall, stays all winter and melts
    /// in early spring (see <see cref="RoofSnow"/>). The edits only exist in memory; nothing is saved.
    /// </remarks>
    internal sealed class TextureBlender
    {
        /*********
        ** Fields
        *********/
        /// <summary>A size larger than any texture, for regions that span a whole texture in one direction.</summary>
        private const int MaxSize = 1 << 16;

        /// <summary>How wide the soft colour fade is, as a share of all pixels (wider is smoother but less clumpy).</summary>
        private const float FadeBand = 0.2f;

        /// <summary>The status text when nothing is being blended.</summary>
        private const string IdleStatus = "normal (no transition today)";
        /// <summary>Converts a pixel's order value into its rank among all pixels (0 to 1), so a share of 0.3 changes about 30% of a texture.</summary>
        private static readonly QuantileTable OrderQuantiles = new((x, y) => GetPixelOrder(0xB1E4u, x, y));

        /// <summary>Buildings without a paint mask whose roof is the same as an upgraded version's that has one (by building ID).</summary>
        private static readonly Dictionary<string, string> SiblingRoofMasks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Barn"] = "Buildings/Deluxe Barn_PaintMask",
            ["Big Barn"] = "Buildings/Deluxe Barn_PaintMask",
            ["Coop"] = "Buildings/Deluxe Coop_PaintMask",
            ["Big Coop"] = "Buildings/Deluxe Coop_PaintMask",
            ["Shed"] = "Buildings/Big Shed_PaintMask"
        };

        /// <summary>The share of a building's own paint mask roof that must land on the building for the mask to be trusted (the game's own masks fit 99.8% or better).</summary>
        private const float OwnMaskFit = 0.9f;

        /// <summary>The same for a mask borrowed from the upgraded building (the game's fit 68% or better, since the upgraded roofs are a bit bigger).</summary>
        private const float SiblingMaskFit = 0.6f;

        /// <summary>Buildings without a paint mask that get a cap of snow along their top (by building ID). Others, like the greenhouse, fish pond and obelisks, are left alone.</summary>
        private static readonly HashSet<string> SnowCapBuildings = new(StringComparer.OrdinalIgnoreCase) { "Silo", "Well", "Mill", "Slime Hutch", "Shipping Bin" };

        /// <summary>Provides simplified APIs for writing mods.</summary>
        private readonly IModHelper Helper;

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;

        /// <summary>The textures currently being blended.</summary>
        private readonly List<BlendSpec> Active = new();

        /// <summary>A key identifying the current plan, so textures are only reloaded when something changes.</summary>
        private string PlanKey = string.Empty;

        /// <summary>The building textures whose roofs already look snowy when loaded (another mod gives them a winter look), by normalized asset name.</summary>
        private readonly HashSet<string> AlreadySnowy = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Accessors
        *********/
        /// <summary>A short description of what's being blended, for the status command.</summary>
        public string Status { get; private set; } = IdleStatus;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public TextureBlender(IModHelper helper, IMonitor monitor)
        {
            this.Helper = helper;
            this.Monitor = monitor;
        }

        /// <summary>Apply the blend when the game loads a texture being blended. Call this from <c>Content.AssetRequested</c>.</summary>
        /// <param name="e">The event arguments.</param>
        public void OnAssetRequested(AssetRequestedEventArgs e)
        {
            foreach (BlendSpec spec in this.Active)
            {
                if (e.NameWithoutLocale.IsEquivalentTo(spec.AssetName))
                {
                    e.Edit(asset => this.Apply(asset, spec), AssetEditPriority.Late);
                    return;
                }
            }
        }

        /// <summary>Recalculate what to blend for today and reload any textures whose blend changed.</summary>
        /// <param name="config">The mod settings.</param>
        public void Update(ModConfig config)
        {
            Plan plan;
            try
            {
                plan = this.BuildPlan(config);
            }
            catch (Exception ex)
            {
                this.Monitor.LogOnce($"Couldn't work out which tree and building textures to change, so they keep their normal look. Technical details:\n{ex}", LogLevel.Warn);
                plan = Plan.Empty;
            }

            if (plan.Key == this.PlanKey)
                return;

            List<BlendSpec> previous = new(this.Active);
            this.Active.Clear();
            this.Active.AddRange(plan.Specs);
            this.PlanKey = plan.Key;
            this.Status = plan.Description;

            // reload the textures the new blends read from first (they may still hold an older blend), then restore textures
            // that are no longer blended, then apply the new blends
            foreach (BlendSpec spec in this.Active)
            {
                if (spec.OtherAssetName != null)
                    this.Invalidate(spec.OtherAssetName);
            }
            foreach (BlendSpec old in previous)
            {
                if (Find(this.Active, old.AssetName) is null)
                    this.Invalidate(old.AssetName);
            }
            foreach (BlendSpec spec in this.Active)
                this.Invalidate(spec.AssetName);

            this.Monitor.Log($"Trees and buildings: {this.Status}.", config.DebugLogging ? LogLevel.Info : LogLevel.Trace);
        }

        /// <summary>Stop blending and restore every texture's normal look (e.g. when returning to the title screen).</summary>
        public void Reset()
        {
            List<BlendSpec> previous = new(this.Active);
            this.Active.Clear();
            this.AlreadySnowy.Clear();
            this.PlanKey = string.Empty;
            this.Status = IdleStatus;

            foreach (BlendSpec old in previous)
                this.Invalidate(old.AssetName);
        }

        /// <summary>Get the order in which a texture pixel changes to the next season's look (lower changes first).</summary>
        /// <param name="seed">The texture's seed.</param>
        /// <param name="x">The pixel X position (relative to its season's area).</param>
        /// <param name="y">The pixel Y position (relative to its season's area).</param>
        /// <remarks>Smooth noise in leaf-sized clumps, so neighbouring pixels change together (with only a hint of per-pixel variation, so clump edges don't speckle).</remarks>
        internal static float GetPixelOrder(uint seed, int x, int y)
        {
            return 0.97f * GroundLayer.ValueNoise(seed, x, y, 9f, 3) + 0.03f * GroundLayer.ToUnit(GroundLayer.Hash(seed, x, y, 77));
        }

        /// <summary>Get how much of the next season's look a pixel has, from 0 (none) to 1 (fully).</summary>
        /// <param name="seed">The texture's seed.</param>
        /// <param name="x">The pixel X position (relative to its season's area).</param>
        /// <param name="y">The pixel Y position (relative to its season's area).</param>
        /// <param name="nextShare">How far the whole texture has turned, from 0 to 1.</param>
        internal static float GetNextPresence(uint seed, int x, int y, float nextShare)
        {
            if (nextShare <= 0f)
                return 0f;
            if (nextShare >= 1f)
                return 1f;

            float rank = OrderQuantiles.GetShare(GetPixelOrder(seed, x, y));
            return Math.Clamp((nextShare - rank) / FadeBand + 0.5f, 0f, 1f);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Work out which textures to blend today, and how much.</summary>
        /// <param name="config">The mod settings.</param>
        private Plan BuildPlan(ModConfig config)
        {
            if ((!config.EnableTreeAndBuildingChanges && !config.EnableRoofSnow) || config.Strength <= 0f || !Context.IsWorldReady)
                return Plan.Empty;

            Season season = Game1.season;
            TransitionInfo info = SeasonCalendar.GetToday(config);
            List<BlendSpec> specs = new();
            List<string> keys = new();
            List<string> descriptions = new();

            // textures are shared by every location, so leave alone any that are also shown in an area keeping its own season
            HashSet<string> keepAsIs = GetFixedSeasonTextures();

            // trees and buildings with their own seasonal looks shift toward the other season during a transition
            if (config.EnableTreeAndBuildingChanges && info.IsActive)
            {
                // how far everything has turned to the next season (keeps rising across the season change)
                bool ramp = info.Phase == TransitionPhase.Ramp;
                float next = TransitionSchedule.GetBlendShare(info, config.RampDays, config.LingerDays);
                float nextShare = Math.Clamp(ramp ? next * config.Strength : 1f - (1f - next) * config.Strength, 0f, 1f);
                if ((ramp && nextShare > 0f) || (!ramp && nextShare < 1f))
                {
                    Season other = ramp ? info.ToSeason : info.FromSeason;
                    AddTrees(specs, season, other, ramp, nextShare, keepAsIs);
                    AddBuildings(specs, season, other, ramp, nextShare, keepAsIs);
                    if (specs.Count > 0)
                    {
                        float otherShare = ramp ? nextShare : 1f - nextShare;
                        keys.Add($"{season}>{other}|{ramp}|{Fmt.Num3(nextShare)}|{specs.Count}");
                        descriptions.Add($"{specs.Count} tree and building textures are about {Fmt.Percent(otherShare)} of the way to their {other} look");
                    }
                }
            }

            // farm buildings have no winter look, so snow builds up on their roofs instead
            float snow = config.EnableRoofSnow ? GetRoofSnow(info, season, config) : 0f;
            if (snow > 0f)
            {
                int roofs = AddRoofSnow(specs, snow, keepAsIs);
                if (roofs > 0)
                {
                    keys.Add($"snow|{Fmt.Num3(snow)}|{roofs}");
                    descriptions.Add($"snow covers about {Fmt.Percent(snow)} of {roofs} building roofs");
                }
            }

            return specs.Count > 0
                ? new Plan(string.Join(";", keys), specs, string.Join("; ", descriptions))
                : Plan.Empty;
        }

        /// <summary>Get the tree and building textures shown in areas that keep their own season (like Ginger Island, which is always summer).</summary>
        /// <remarks>
        /// Every location shares the same textures, so changing one of these would change it in that area too. This only counts areas
        /// whose season differs from the valley's today: an area locked to the valley's current season (like Ginger Island in summer)
        /// would otherwise stop late-summer leaf colours for that tree type everywhere, which matters more than a few early fall
        /// leaves there (expansion mods add summer-locked areas with ordinary oaks and maples).
        /// </remarks>
        private static HashSet<string> GetFixedSeasonTextures()
        {
            HashSet<string> textures = new(StringComparer.OrdinalIgnoreCase);
            Utility.ForEachLocation(location =>
            {
                Season locationSeason = location.GetSeason();
                if (locationSeason == Game1.season)
                    return true;

                foreach (TerrainFeature feature in location.terrainFeatures.Values)
                {
                    if (feature is Tree tree && GetTreeTexture(tree.GetData(), locationSeason) is string treeTexture)
                        textures.Add(PathUtilities.NormalizeAssetName(treeTexture));
                }
                foreach (Building building in location.buildings)
                {
                    string? buildingTexture = building.textureName();
                    if (!string.IsNullOrWhiteSpace(buildingTexture))
                        textures.Add(PathUtilities.NormalizeAssetName(buildingTexture));
                }
                return true;
            }, includeInteriors: false);

            return textures;
        }

        /// <summary>Get how much of the farm buildings' roofs is under snow, from 0 to 1.</summary>
        /// <param name="info">Today's transition state.</param>
        /// <param name="season">The current season.</param>
        /// <param name="config">The mod settings.</param>
        /// <remarks>Snow settles through late fall, covers the roofs all winter, and melts through late winter and early spring, following the same timing as the map.</remarks>
        internal static float GetRoofSnow(in TransitionInfo info, Season season, ModConfig config)
        {
            if (!info.IsActive)
                return season == Season.Winter ? 1f : 0f;

            bool ramp = info.Phase == TransitionPhase.Ramp;
            float next = TransitionSchedule.GetBlendShare(info, config.RampDays, config.LingerDays);
            float nextShare = Math.Clamp(ramp ? next * config.Strength : 1f - (1f - next) * config.Strength, 0f, 1f);
            if (info.ToSeason == Season.Winter)
                return nextShare;
            if (info.FromSeason == Season.Winter)
                return 1f - nextShare;
            return 0f;
        }

        /// <summary>Add snow to the roofs of every building that doesn't have a winter look of its own.</summary>
        /// <param name="specs">The blends to add to.</param>
        /// <param name="amount">How much of each roof is covered, from 0 to 1.</param>
        /// <returns>The number of building textures given snow.</returns>
        private static int AddRoofSnow(List<BlendSpec> specs, float amount, HashSet<string> keepAsIs)
        {
            int count = 0;
            foreach ((string id, BuildingData data) in Game1.buildingData)
            {
                // buildings like the pet bowl already have a winter version
                if (data is null || data.SeasonOffset != Point.Zero)
                    continue;

                if (AddRoofSnow(specs, id, data.Texture, data.SourceRect, amount, isDefaultTexture: true, keepAsIs))
                    count++;
                if (data.Skins is null)
                    continue;
                foreach (BuildingSkin skin in data.Skins)
                {
                    if (AddRoofSnow(specs, id, skin?.Texture, data.SourceRect, amount, isDefaultTexture: false, keepAsIs))
                        count++;
                }
            }

            return count;
        }

        /// <summary>Add snow to one building texture's roof, if this mod knows where its roof is.</summary>
        /// <param name="specs">The blends to add to.</param>
        /// <param name="buildingId">The building type ID.</param>
        /// <param name="texture">The building texture's asset name.</param>
        /// <param name="sourceRect">The area the game draws for the building.</param>
        /// <param name="amount">How much of the roof is covered, from 0 to 1.</param>
        /// <param name="isDefaultTexture">Whether this is the building's normal texture (not a skin).</param>
        /// <param name="keepAsIs">Textures to leave alone (see <see cref="GetFixedSeasonTextures"/>).</param>
        private static bool AddRoofSnow(List<BlendSpec> specs, string buildingId, string? texture, Rectangle sourceRect, float amount, bool isDefaultTexture, HashSet<string> keepAsIs)
        {
            if (string.IsNullOrWhiteSpace(texture) || Find(specs, texture) != null || keepAsIs.Contains(PathUtilities.NormalizeAssetName(texture)))
                return false;

            uint seed = GroundLayer.StableHash("roof:" + PathUtilities.NormalizeAssetName(texture));

            // its own paint mask says exactly where the roof is (and is edited too, so painting the building doesn't tint the snow)
            string ownMask = texture + "_PaintMask";
            if (AssetExists(ownMask))
            {
                specs.Add(BlendSpec.ForRoofSnow(texture, ownMask, sourceRect, seed, amount, OwnMaskFit));
                if (Find(specs, ownMask) is null)
                    specs.Add(BlendSpec.ForSnowyRoofMask(ownMask, texture, sourceRect, seed, amount, OwnMaskFit));
                return true;
            }

            // the basic barn, coop and shed have the same roof as their upgraded version (which is a bit bigger)
            if (isDefaultTexture && SiblingRoofMasks.TryGetValue(buildingId, out string? siblingMask) && AssetExists(siblingMask))
            {
                specs.Add(BlendSpec.ForRoofSnow(texture, siblingMask, sourceRect, seed, amount, SiblingMaskFit));
                return true;
            }

            // small buildings without a mask get a cap of snow along their top
            if (SnowCapBuildings.Contains(buildingId))
            {
                specs.Add(BlendSpec.ForRoofSnow(texture, null, sourceRect, seed, amount, OwnMaskFit));
                return true;
            }

            return false;
        }

        /// <summary>Get whether an asset exists, without loading it.</summary>
        private static bool AssetExists(string assetName)
        {
            try
            {
                return Game1.content.DoesAssetExist<Texture2D>(assetName);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Add the wild tree textures (oak, maple, pine, mahogany, and any modded trees with seasonal textures).</summary>
        private static void AddTrees(List<BlendSpec> specs, Season season, Season other, bool ramp, float nextShare, HashSet<string> keepAsIs)
        {
            foreach ((string id, WildTreeData data) in Tree.GetWildTreeDataDictionary())
            {
                string? current = GetTreeTexture(data, season);
                string? target = GetTreeTexture(data, other);
                if (current is null || target is null || IsSameAsset(current, target) || Find(specs, current) != null || keepAsIs.Contains(PathUtilities.NormalizeAssetName(current)))
                    continue;

                BlendSpec spec = new(current, target, GroundLayer.StableHash("tree:" + id), ramp, nextShare);
                foreach (Season sibling in new[] { Season.Spring, Season.Summer, Season.Fall, Season.Winter })
                {
                    string? texture = GetTreeTexture(data, sibling);
                    if (texture != null && !IsSameAsset(texture, current) && !IsSameAsset(texture, target) && !spec.SiblingAssetNames.Exists(name => IsSameAsset(name, texture)))
                        spec.SiblingAssetNames.Add(texture);
                }
                specs.Add(spec);
            }
        }

        /// <summary>Get the texture a wild tree uses in a season, the same way the game chooses it (ignoring conditional textures).</summary>
        /// <param name="data">The tree data.</param>
        /// <param name="season">The season.</param>
        private static string? GetTreeTexture(WildTreeData? data, Season season)
        {
            if (data?.Textures is null || data.Textures.Count == 0)
                return null;

            foreach (WildTreeTextureData texture in data.Textures)
            {
                if (texture is null || string.IsNullOrWhiteSpace(texture.Texture) || !string.IsNullOrWhiteSpace(texture.Condition))
                    continue;
                if (texture.Season.HasValue && texture.Season.Value != season)
                    continue;

                return texture.Texture;
            }

            return data.Textures[0]?.Texture;
        }

        /// <summary>Add the building textures which have seasonal versions side by side in one image (like the farmhouse).</summary>
        private static void AddBuildings(List<BlendSpec> specs, Season season, Season other, bool ramp, float nextShare, HashSet<string> keepAsIs)
        {
            int current = (int)season;
            int target = (int)other;

            foreach (BuildingData data in Game1.buildingData.Values)
            {
                if (data is null || string.IsNullOrWhiteSpace(data.Texture) || data.SeasonOffset == Point.Zero || data.SourceRect.IsEmpty || keepAsIs.Contains(PathUtilities.NormalizeAssetName(data.Texture)))
                    continue;

                // the part of the image used this season, and where the other season's version is
                Point step = data.SeasonOffset;
                bool horizontal = step.X != 0;
                Rectangle area = horizontal
                    ? new Rectangle(data.SourceRect.X + step.X * current, 0, Math.Abs(step.X), MaxSize)
                    : new Rectangle(0, data.SourceRect.Y + step.Y * current, MaxSize, Math.Abs(step.Y));
                Point offset = new(step.X * (target - current), step.Y * (target - current));

                BlendSpec? spec = Find(specs, data.Texture);
                if (spec is null)
                {
                    spec = new BlendSpec(data.Texture, null, GroundLayer.StableHash("building:" + PathUtilities.NormalizeAssetName(data.Texture)), ramp, nextShare);
                    specs.Add(spec);
                }
                spec.Regions.Add(new BlendRegion(area, offset, horizontal));
            }
        }

        /// <summary>Blend a texture as it loads.</summary>
        /// <param name="asset">The texture being loaded.</param>
        /// <param name="spec">How to blend it.</param>
        private void Apply(IAssetData asset, BlendSpec spec)
        {
            if (spec.Kind != BlendKind.Season)
            {
                this.ApplySnow(asset, spec);
                return;
            }

            try
            {
                IAssetDataForImage image = asset.AsImage();
                Texture2D texture = image.Data;
                int width = texture.Width;
                int height = texture.Height;
                Color[] pixels = new Color[width * height];
                texture.GetData(pixels);

                // get the other season's pixels
                Color[] source;
                int sourceWidth;
                int sourceHeight;
                if (spec.OtherAssetName is null)
                {
                    source = (Color[])pixels.Clone();
                    sourceWidth = width;
                    sourceHeight = height;
                }
                else
                {
                    Texture2D other = this.Helper.GameContent.Load<Texture2D>(spec.OtherAssetName);
                    sourceWidth = other.Width;
                    sourceHeight = other.Height;
                    source = new Color[sourceWidth * sourceHeight];
                    other.GetData(source);
                }

                // another mod's look for this season only (the other season's texture doesn't have it yet, like a Content Patcher
                // edit with the {{season}} token) stays until the season changes, so the two looks don't mix
                bool[]? keep = null;
                if (spec.OtherAssetName != null)
                {
                    KeptPixels? kept = this.GetKeptPixels(asset.NameWithoutLocale.BaseName, pixels, width, height, spec.OtherAssetName, source, sourceWidth, sourceHeight, spec.SiblingAssetNames);
                    if (kept != null)
                    {
                        this.Monitor.LogOnce($"Another mod changed {kept.Count} pixels of {asset.NameWithoutLocale} without changing {spec.OtherAssetName} to match (it probably only applies to the current season), so those pixels keep their look until the season changes.", LogLevel.Trace);
                        keep = kept.Pixels;
                    }
                }

                // blend
                int changed = 0;
                Rectangle bounds = new(0, 0, width, height);
                if (spec.Regions.Count == 0)
                    changed = Blend(pixels, width, source, sourceWidth, sourceHeight, bounds, Point.Zero, Point.Zero, spec, keep);
                else
                {
                    foreach (BlendRegion region in spec.Regions)
                    {
                        Rectangle area = Rectangle.Intersect(region.Area, bounds);
                        if (area.IsEmpty)
                            continue;

                        Point origin = region.Horizontal ? new Point(region.Area.X, 0) : new Point(0, region.Area.Y);
                        changed += Blend(pixels, width, source, sourceWidth, sourceHeight, area, region.Offset, origin, spec, keep);
                    }
                }

                if (changed > 0)
                    texture.SetData(pixels);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't blend {asset.NameWithoutLocale} toward the next season, so it keeps its normal look. Technical details:\n{ex}", LogLevel.Warn);
            }
        }

        /// <summary>Put snow on a building texture's roof, or mark the snowy roof pixels in its paint mask.</summary>
        /// <param name="asset">The texture being loaded.</param>
        /// <param name="spec">What to do.</param>
        private void ApplySnow(IAssetData asset, BlendSpec spec)
        {
            try
            {
                IAssetDataForImage image = asset.AsImage();
                Texture2D texture = image.Data;
                int width = texture.Width;
                int height = texture.Height;
                Color[] pixels = new Color[width * height];
                texture.GetData(pixels);

                int changed;
                if (spec.Kind == BlendKind.SnowyRoofMask)
                {
                    // mark exactly the mask pixels that get snow on the building (the mask is this asset)
                    Color[]? building = this.TryGetPixels(spec.RoofTextureName, width, height);
                    if (building is null)
                        return;
                    if (spec.RoofTextureName != null && this.AlreadySnowy.Contains(PathUtilities.NormalizeAssetName(spec.RoofTextureName)))
                        return; // the building has its own winter look (see below), so its paint mask stays as it is
                    bool[] roof = RoofSnow.FindRoof(building, width, height, pixels, spec.SourceRect, spec.Seed, spec.MinMaskFit, out _);
                    changed = RoofSnow.MarkSnowyRoof(pixels, width, roof, spec.Seed, spec.NextShare);
                }
                else
                {
                    Color[]? mask = this.TryGetPixels(spec.MaskAssetName, width, height);
                    bool[] roof = RoofSnow.FindRoof(pixels, width, height, mask, spec.SourceRect, spec.Seed, spec.MinMaskFit, out int mismatchedFrames);

                    // another mod may already give this building a winter look (like a seasonal buildings mod), so don't add snow on top
                    string name = PathUtilities.NormalizeAssetName(asset.NameWithoutLocale.Name);
                    if (RoofSnow.GetSnowyShare(pixels, roof) >= RoofSnow.AlreadySnowyShare)
                    {
                        this.AlreadySnowy.Add(name);
                        this.Monitor.LogOnce($"The roof of {asset.NameWithoutLocale} already looks snowy (another mod probably gives it a winter look), so no snow is added to it.", LogLevel.Trace);
                        return;
                    }
                    this.AlreadySnowy.Remove(name);

                    if (mismatchedFrames > 0)
                        this.Monitor.LogOnce($"The roof of {asset.NameWithoutLocale} doesn't match its paint mask (another mod probably changed how it looks), so it gets a thin cap of snow instead. You can turn roof snow off in the config if it doesn't look right.", LogLevel.Trace);
                    changed = RoofSnow.Apply(pixels, width, roof, spec.Seed, spec.NextShare);
                }

                if (changed > 0)
                    texture.SetData(pixels);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't put snow on {asset.NameWithoutLocale}, so it keeps its normal look. Technical details:\n{ex}", LogLevel.Warn);
            }
        }

        /// <summary>Get another texture's pixels, if it exists and is the same size (like a building's paint mask).</summary>
        /// <param name="assetName">The texture's asset name, or <c>null</c> for none.</param>
        /// <param name="width">The expected width.</param>
        /// <param name="height">The expected height.</param>
        private Color[]? TryGetPixels(string? assetName, int width, int height)
        {
            if (assetName is null)
                return null;

            try
            {
                Texture2D texture = this.Helper.GameContent.Load<Texture2D>(assetName);
                if (texture.Width != width || texture.Height != height)
                    return null;

                Color[] pixels = new Color[width * height];
                texture.GetData(pixels);
                return pixels;
            }
            catch
            {
                return null; // a building without a usable mask gets a snow cap instead
            }
        }

        /// <summary>Find which pixels of a texture are another mod's look for this season only, which the other season's texture doesn't have yet.</summary>
        /// <param name="assetName">This season's asset name.</param>
        /// <param name="pixels">This season's pixels, with every other mod's changes.</param>
        /// <param name="width">This season's texture width.</param>
        /// <param name="height">This season's texture height.</param>
        /// <param name="otherAssetName">The other season's asset name.</param>
        /// <param name="source">The other season's pixels.</param>
        /// <param name="sourceWidth">The other season's texture width (the winter oak is narrower than the others, for example).</param>
        /// <param name="sourceHeight">The other season's texture height.</param>
        /// <param name="siblingAssetNames">The remaining seasons' textures.</param>
        /// <returns>The pixels to keep, or <c>null</c> if there are none (or it can't be told, like for a mod's own texture).</returns>
        private KeptPixels? GetKeptPixels(string assetName, Color[] pixels, int width, int height, string otherAssetName, Color[] source, int sourceWidth, int sourceHeight, IReadOnlyList<string> siblingAssetNames)
        {
            Color[]? vanilla = VanillaArt.TryLoad(assetName, this.Monitor, out int vanillaWidth, out int vanillaHeight);
            if (vanilla is null)
                return null;
            Color[]? vanillaOther = VanillaArt.TryLoad(otherAssetName, this.Monitor, out int otherWidth, out int otherHeight);
            if (vanillaOther is null)
                return null;

            List<SeasonArt> rest = new();
            foreach (string sibling in siblingAssetNames)
            {
                Color[]? vanillaSibling = VanillaArt.TryLoad(sibling, this.Monitor, out int siblingVanillaWidth, out int siblingVanillaHeight);
                if (vanillaSibling is null)
                    continue;
                Texture2D texture = this.Helper.GameContent.Load<Texture2D>(sibling);
                Color[] siblingPixels = new Color[texture.Width * texture.Height];
                texture.GetData(siblingPixels);
                rest.Add(new SeasonArt(siblingPixels, texture.Width, texture.Height, vanillaSibling, siblingVanillaWidth, siblingVanillaHeight));
            }

            return KeptPixels.Find(
                new SeasonArt(pixels, width, height, vanilla, vanillaWidth, vanillaHeight),
                new SeasonArt(source, sourceWidth, sourceHeight, vanillaOther, otherWidth, otherHeight),
                rest
            );
        }

        /// <summary>Blend one area of a texture toward the other season's pixels.</summary>
        /// <param name="keep">Which pixels to leave as they are (see <see cref="KeptPixels"/>), or <c>null</c> for none.</param>
        /// <returns>Returns the number of pixels changed.</returns>
        private static int Blend(Color[] pixels, int width, Color[] source, int sourceWidth, int sourceHeight, Rectangle area, Point offset, Point origin, BlendSpec spec, bool[]? keep)
        {
            int changed = 0;
            for (int y = area.Top; y < area.Bottom; y++)
            {
                int sourceY = y + offset.Y;
                if (sourceY < 0 || sourceY >= sourceHeight)
                    continue;

                for (int x = area.Left; x < area.Right; x++)
                {
                    int sourceX = x + offset.X;
                    if (sourceX < 0 || sourceX >= sourceWidth)
                        continue;

                    int index = y * width + x;
                    if (keep != null && keep[index])
                        continue;

                    Color current = pixels[index];
                    Color other = source[sourceY * sourceWidth + sourceX];
                    if (current == other)
                        continue;

                    // the earlier and later season's version of this pixel
                    Color earlier = spec.Ramp ? current : other;
                    Color later = spec.Ramp ? other : current;
                    float presence = GetNextPresence(spec.Seed, x - origin.X, y - origin.Y, spec.NextShare);

                    // fade the colour where both versions have something here; otherwise change the whole clump at once
                    Color result = earlier.A >= 200 && later.A >= 200
                        ? Color.Lerp(earlier, later, presence)
                        : (presence >= 0.5f ? later : earlier);

                    if (result != current)
                    {
                        pixels[index] = result;
                        changed++;
                    }
                }
            }

            return changed;
        }

        /// <summary>Reload a texture so it picks up (or drops) a blend. SMAPI copies the new pixels into the texture the game is already using.</summary>
        /// <param name="assetName">The asset name.</param>
        private void Invalidate(string assetName)
        {
            try
            {
                string normalized = PathUtilities.NormalizeAssetName(assetName);
                this.Helper.GameContent.InvalidateCache(asset => asset.NameWithoutLocale.IsEquivalentTo(normalized));
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Couldn't reload {assetName}. Technical details:\n{ex}", LogLevel.Warn);
            }
        }

        /// <summary>Find the blend for a texture in a list, if any.</summary>
        private static BlendSpec? Find(List<BlendSpec> specs, string assetName)
        {
            foreach (BlendSpec spec in specs)
            {
                if (IsSameAsset(spec.AssetName, assetName))
                    return spec;
            }

            return null;
        }

        /// <summary>Get whether two asset names refer to the same asset.</summary>
        private static bool IsSameAsset(string left, string right)
        {
            return string.Equals(PathUtilities.NormalizeAssetName(left), PathUtilities.NormalizeAssetName(right), StringComparison.OrdinalIgnoreCase);
        }


        /*********
        ** Private types
        *********/
        /// <summary>What a blend does to its texture.</summary>
        private enum BlendKind
        {
            /// <summary>Shift the texture toward another season's version.</summary>
            Season,

            /// <summary>Put snow on a building's roof.</summary>
            RoofSnow,

            /// <summary>Mark the snowy roof pixels in a building's paint mask, so painting doesn't tint the snow.</summary>
            SnowyRoofMask
        }

        /// <summary>How to blend one texture.</summary>
        private sealed class BlendSpec
        {
            /// <summary>What the blend does.</summary>
            public BlendKind Kind { get; }

            /// <summary>The texture to edit (the one the game shows this season).</summary>
            public string AssetName { get; }

            /// <summary>The other season's texture to take pixels from, or <c>null</c> if both seasons are in the same image (see <see cref="Regions"/>).</summary>
            public string? OtherAssetName { get; }

            /// <summary>For roof snow, the paint mask that says where the roof is, or <c>null</c> to use a snow cap.</summary>
            public string? MaskAssetName { get; }

            /// <summary>For roof snow, the area the game draws for the building.</summary>
            public Rectangle SourceRect { get; }

            /// <summary>For roof snow, the share of the mask's roof that must land on the building for the mask to be trusted.</summary>
            public float MinMaskFit { get; }

            /// <summary>For a paint mask edit, the building texture whose roof it goes with.</summary>
            public string? RoofTextureName { get; }

            /// <summary>The seed for the pixel order.</summary>
            public uint Seed { get; }

            /// <summary>Whether this is the end of a season (the edited texture is the earlier season) rather than the start.</summary>
            public bool Ramp { get; }

            /// <summary>How far the texture has turned to the next season, from 0 to 1 (for roof snow, how much of the roof is covered).</summary>
            public float NextShare { get; }

            /// <summary>The areas to blend when both seasons are in the same image; empty to blend the whole image from <see cref="OtherAssetName"/>.</summary>
            public List<BlendRegion> Regions { get; } = new();

            /// <summary>For a seasonal texture, the remaining seasons' versions (to tell a recolour of every season from another mod's look for this season only, see <see cref="KeptPixels"/>).</summary>
            public List<string> SiblingAssetNames { get; } = new();

            /// <summary>Construct a seasonal blend.</summary>
            public BlendSpec(string assetName, string? otherAssetName, uint seed, bool ramp, float nextShare)
                : this(BlendKind.Season, assetName, otherAssetName, null, null, Rectangle.Empty, 0f, seed, ramp, nextShare) { }

            /// <summary>Construct an instance.</summary>
            private BlendSpec(BlendKind kind, string assetName, string? otherAssetName, string? maskAssetName, string? roofTextureName, Rectangle sourceRect, float minMaskFit, uint seed, bool ramp, float nextShare)
            {
                this.Kind = kind;
                this.AssetName = assetName;
                this.OtherAssetName = otherAssetName;
                this.MaskAssetName = maskAssetName;
                this.RoofTextureName = roofTextureName;
                this.SourceRect = sourceRect;
                this.MinMaskFit = minMaskFit;
                this.Seed = seed;
                this.Ramp = ramp;
                this.NextShare = nextShare;
            }

            /// <summary>Snow on a building's roof.</summary>
            /// <param name="texture">The building texture.</param>
            /// <param name="mask">The paint mask that says where the roof is, or <c>null</c> to use a snow cap.</param>
            /// <param name="sourceRect">The area the game draws for the building.</param>
            /// <param name="seed">The seed for the snow clumps.</param>
            /// <param name="amount">How much of the roof is covered, from 0 to 1.</param>
            /// <param name="minMaskFit">The share of the mask's roof that must land on the building for the mask to be trusted.</param>
            public static BlendSpec ForRoofSnow(string texture, string? mask, Rectangle sourceRect, uint seed, float amount, float minMaskFit)
            {
                return new BlendSpec(BlendKind.RoofSnow, texture, null, mask, null, sourceRect, minMaskFit, seed, ramp: true, amount);
            }

            /// <summary>The paint mask edit that goes with a building's roof snow (same seed, amount and fit).</summary>
            /// <param name="mask">The paint mask.</param>
            /// <param name="texture">The building texture whose roof it goes with.</param>
            /// <param name="sourceRect">The area the game draws for the building.</param>
            /// <param name="seed">The seed used for the building's roof snow.</param>
            /// <param name="amount">The amount used for the building's roof snow.</param>
            /// <param name="minMaskFit">The fit used for the building's roof snow.</param>
            public static BlendSpec ForSnowyRoofMask(string mask, string texture, Rectangle sourceRect, uint seed, float amount, float minMaskFit)
            {
                return new BlendSpec(BlendKind.SnowyRoofMask, mask, null, null, texture, sourceRect, minMaskFit, seed, ramp: true, amount);
            }
        }

        /// <summary>An area of an image used this season, and where the other season's version is.</summary>
        private readonly struct BlendRegion
        {
            /// <summary>The area used this season.</summary>
            public Rectangle Area { get; }

            /// <summary>The offset from a pixel in <see cref="Area"/> to the same pixel in the other season's version.</summary>
            public Point Offset { get; }

            /// <summary>Whether the seasons are side by side (instead of stacked).</summary>
            public bool Horizontal { get; }

            /// <summary>Construct an instance.</summary>
            public BlendRegion(Rectangle area, Point offset, bool horizontal)
            {
                this.Area = area;
                this.Offset = offset;
                this.Horizontal = horizontal;
            }
        }

        /// <summary>What to blend today.</summary>
        private sealed class Plan
        {
            /// <summary>Nothing to blend.</summary>
            public static readonly Plan Empty = new(string.Empty, new List<BlendSpec>(), IdleStatus);

            /// <summary>A key identifying the plan.</summary>
            public string Key { get; }

            /// <summary>The textures to blend.</summary>
            public List<BlendSpec> Specs { get; }

            /// <summary>A short description for the status command.</summary>
            public string Description { get; }

            /// <summary>Construct an instance.</summary>
            public Plan(string key, List<BlendSpec> specs, string description)
            {
                this.Key = key;
                this.Specs = specs;
                this.Description = description;
            }
        }
    }
}
