using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.TerrainFeatures;
using xTile;
using xTile.Layers;

namespace GradualSeasons
{
    /// <summary>What lies on, or grows from, the ground during a transition (drawn with the mod's own sprites).</summary>
    /// <remarks>Snow, thaw and the season's ground colour itself come from the game's own tiles; see <see cref="SeasonTileLayer"/>.</remarks>
    internal enum GroundCoverKind : byte
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>Fallen leaves: autumn leaves in late summer, the last brown leaves in early winter.</summary>
        FallenLeaves,

        /// <summary>Fallen cherry petals left over from spring (early summer).</summary>
        FallenPetals,

        /// <summary>Summer wildflowers and deep green grass sprouting (late spring).</summary>
        SummerFlowers
    }

    /// <summary>The ground cover that should be visible.</summary>
    internal readonly struct GroundCoverTarget
    {
        /// <summary>No ground cover.</summary>
        public static readonly GroundCoverTarget None = default;

        /// <summary>What's on the ground.</summary>
        public GroundCoverKind Kind { get; }

        /// <summary>The fraction of open ground covered, from 0 to 1.</summary>
        public float Coverage { get; }

        /// <summary>Which leaf colours to use for <see cref="GroundCoverKind.FallenLeaves"/>.</summary>
        public LeafPalette LeafPalette { get; }

        /// <summary>Construct an instance.</summary>
        public GroundCoverTarget(GroundCoverKind kind, float coverage, LeafPalette leafPalette)
        {
            this.Kind = kind;
            this.Coverage = coverage;
            this.LeafPalette = leafPalette;
        }
    }

    /// <summary>Draws fallen leaves, fallen petals and summer flowers on the map's ground, under everything standing on it.</summary>
    /// <remarks>
    /// This draws the mod's own sprites on top of the ground right after the game draws it, so the game's map textures are
    /// never changed. Because it's drawn before the game's lighting, it gets darker at night automatically. Everything here is
    /// cosmetic and local: nothing is synced or saved. It also holds the noise helpers the other layers share.
    /// </remarks>
    internal sealed class GroundLayer
    {
        /*********
        ** Fields
        *********/
        /// <summary>The size of a map tile in screen pixels.</summary>
        private const int TileSize = Game1.tileSize;

        /// <summary>How much coverage it takes for a tile's cover to fade in fully once its threshold is crossed (no popping).</summary>
        private const float FadeBand = 0.02f;

        /// <summary>How fast the drawn coverage follows a change in the target, per second (leaves visibly pile up).</summary>
        private const float CoverageSpeed = 0.35f;

        /// <summary>The <see cref="GroundCache"/> threshold for tiles which never get cover.</summary>
        private const float Ineligible = 2f;

        /// <summary>A tile flag: things can lie here (leaves, petals).</summary>
        private const byte LyingFlag = 1;

        /// <summary>A tile flag: things can grow here (grass and flowers need grass or dirt).</summary>
        private const byte GrowingFlag = 2;

        /// <summary>How many locations' tile data each screen keeps.</summary>
        private const int MaxCachedLocations = 12;

        /// <summary>The fallen leaf sprites for late summer.</summary>
        private static readonly byte[] AutumnLeafSprites = { SpriteFactory.LeafOrange, SpriteFactory.LeafRed, SpriteFactory.LeafYellow };

        /// <summary>The fallen leaf sprites for early winter.</summary>
        private static readonly byte[] LastLeafSprites = { SpriteFactory.LeafBrown, SpriteFactory.LeafBrown, SpriteFactory.LeafOrange };

        /// <summary>The fallen petal sprites.</summary>
        private static readonly byte[] PetalSprites = { SpriteFactory.PetalLight, SpriteFactory.PetalDeep };

        /// <summary>The centre of a particle cell, used as the rotation origin for fallen leaves and petals.</summary>
        private static readonly Vector2 ParticleOrigin = new(SpriteFactory.ParticleCellSize / 2f, SpriteFactory.ParticleCellSize / 2f);

        /// <summary>The four quarter-turn rotations (fallen leaves only use these, so the pixel art stays crisp).</summary>
        private static readonly float[] QuarterTurns = { 0f, MathF.PI / 2f, MathF.PI, MathF.PI * 1.5f };

        /// <summary>The ground cover state for each split-screen player.</summary>
        private readonly PerScreen<ScreenState> Screens = new(() => new ScreenState());

        /// <summary>Incremented to make every screen reset on its next frame.</summary>
        private int Generation;


        /*********
        ** Public methods
        *********/
        /// <summary>Reset every screen and forget the cached tile data (e.g. when returning to the title screen).</summary>
        public void ResetAll()
        {
            this.Generation++;
        }

        /// <summary>Stop drawing on the current screen. The next drawn frame starts fresh at the target coverage.</summary>
        public void Hide()
        {
            this.Screens.Value.Location = null;
        }

        /// <summary>Calculate which ground cover should be visible, and how much.</summary>
        /// <param name="info">The transition state to show.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="weather">The vanilla weather right now.</param>
        public static GroundCoverTarget ComputeTarget(in TransitionInfo info, ModConfig config, in VanillaWeather weather)
        {
            if (!info.IsActive || config.Strength <= 0f)
                return GroundCoverTarget.None;

            GroundCoverKind kind;
            float scale;
            float weatherFactor = 1f;
            LeafPalette palette = LeafPalette.Autumn;
            bool ramp = info.Phase == TransitionPhase.Ramp;

            switch (info.Kind)
            {
                case TransitionKind.SpringToSummer:
                    if (ramp)
                    {
                        // late spring: summer wildflowers sprout
                        kind = GroundCoverKind.SummerFlowers;
                        scale = 0.30f;
                    }
                    else
                    {
                        // early summer: fallen cherry petals left over from spring
                        kind = GroundCoverKind.FallenPetals;
                        scale = 0.45f;
                    }
                    break;

                case TransitionKind.SummerToFall when ramp:
                    // late summer: fallen autumn leaves pile up
                    kind = GroundCoverKind.FallenLeaves;
                    scale = 0.55f;
                    break;

                case TransitionKind.FallToWinter when !ramp:
                    // early winter: the last brown and orange leaves lie on the snow; fresh snow buries most of them
                    kind = GroundCoverKind.FallenLeaves;
                    palette = LeafPalette.LastLeaves;
                    scale = 0.50f;
                    if (weather.Snowing)
                        weatherFactor = 0.4f;
                    break;

                default:
                    return GroundCoverTarget.None;
            }

            float coverage = Math.Clamp(info.Intensity * config.Strength * scale * weatherFactor, 0f, 1f);
            return coverage > 0f
                ? new GroundCoverTarget(kind, coverage, palette)
                : GroundCoverTarget.None;
        }

        /// <summary>Update and draw the ground cover for the current screen. Call this right after the game draws the ground (<c>RenderSteps.World_Background</c>).</summary>
        /// <param name="spriteBatch">The sprite batch being drawn.</param>
        /// <param name="groundTexture">The ground cover sprite sheet, if loaded.</param>
        /// <param name="particleTexture">The particle sprite sheet (used for fallen leaves and petals), if loaded.</param>
        /// <param name="info">The transition state to show.</param>
        /// <param name="config">The mod settings.</param>
        /// <param name="location">The current location (outdoors, in the valley).</param>
        /// <param name="enabled">Whether ground cover is wanted at all; if not, it fades away.</param>
        public void Draw(SpriteBatch spriteBatch, Texture2D? groundTexture, Texture2D? particleTexture, in TransitionInfo info, ModConfig config, GameLocation location, bool enabled)
        {
            ScreenState state = this.Screens.Value;
            if (state.Generation != this.Generation)
            {
                state.Generation = this.Generation;
                state.Reset();
            }

            VanillaWeather weather = VanillaWeather.For(location);
            GroundCoverTarget target = enabled ? ComputeTarget(info, config, weather) : GroundCoverTarget.None;
            state.Target = target;

            // after a warp the screen was black, so jump straight to the target; otherwise animate towards it
            if (!ReferenceEquals(state.Location, location))
            {
                state.Location = location;
                state.Kind = target.Kind;
                state.Coverage = target.Coverage;
                state.LeafPalette = target.LeafPalette;
            }
            else
            {
                GameTime? gameTime = Game1.currentGameTime;
                float elapsed = gameTime is null ? 0f : (float)gameTime.ElapsedGameTime.TotalSeconds;
                float step = CoverageSpeed * Math.Clamp(elapsed, 0f, 0.1f);

                if (state.Kind != target.Kind)
                {
                    // clear the old cover before the new kind grows
                    state.Coverage = Math.Max(0f, state.Coverage - step * 2f);
                    if (state.Coverage <= 0f)
                    {
                        state.Kind = target.Kind;
                        state.LeafPalette = target.LeafPalette;
                    }
                }
                else
                {
                    state.Coverage = state.Coverage < target.Coverage
                        ? Math.Min(target.Coverage, state.Coverage + step)
                        : Math.Max(target.Coverage, state.Coverage - step);
                    state.LeafPalette = target.LeafPalette;
                }
            }

            if (state.Kind == GroundCoverKind.None || state.Coverage <= 0f)
                return;

            // get the texture and tile data
            Texture2D? texture = state.Kind == GroundCoverKind.SummerFlowers ? groundTexture : particleTexture;
            if (texture is null)
                return;

            GroundCache? cache = this.GetCache(state, location);
            if (cache is null || cache.Width == 0)
                return;

            DrawTiles(spriteBatch, texture, state, cache, location);
        }

        /// <summary>Get the ground cover on the current screen, for the status command.</summary>
        /// <param name="kind">What's being drawn.</param>
        /// <param name="coverage">How much is being drawn right now (it moves towards the target over a few seconds).</param>
        public void GetScreenStatus(out GroundCoverKind kind, out float coverage)
        {
            ScreenState state = this.Screens.Value;
            kind = state.Kind;
            coverage = state.Coverage;
        }

        /// <summary>Describe a transition's ground decorations, for the console commands.</summary>
        /// <param name="info">The transition state.</param>
        public static string DescribeEffect(in TransitionInfo info)
        {
            return (info.Kind, info.Phase) switch
            {
                (TransitionKind.SpringToSummer, TransitionPhase.Ramp) => "summer wildflowers sprouting",
                (TransitionKind.SpringToSummer, TransitionPhase.Linger) => "fallen cherry petals on the ground",
                (TransitionKind.SummerToFall, TransitionPhase.Ramp) => "fallen orange, red and yellow leaves piling up",
                (TransitionKind.FallToWinter, TransitionPhase.Linger) => "the last brown and orange leaves lying on the snow",
                (TransitionKind.None, _) => "nothing (no transition)",
                _ => "nothing extra (the ground itself changes)"
            };
        }

        /// <summary>Get a short name for a kind of ground cover.</summary>
        /// <param name="kind">The ground cover kind.</param>
        public static string GetName(GroundCoverKind kind)
        {
            return kind switch
            {
                GroundCoverKind.FallenLeaves => "fallen leaves",
                GroundCoverKind.FallenPetals => "fallen petals",
                GroundCoverKind.SummerFlowers => "summer flowers",
                _ => "nothing"
            };
        }

        /// <summary>Get how many fallen leaves or petals to draw on a tile.</summary>
        /// <param name="margin">How far the coverage is past the tile's threshold.</param>
        /// <param name="max">The most to draw on one tile.</param>
        internal static int GetScatterCount(float margin, int max)
        {
            return 1 + Math.Min(max - 1, (int)(margin / 0.09f));
        }

        /// <summary>Get a stable hash for a tile, so the same tile always looks the same.</summary>
        /// <param name="seed">The location seed.</param>
        /// <param name="x">The tile X position.</param>
        /// <param name="y">The tile Y position.</param>
        /// <param name="salt">A number to get different hashes for the same tile.</param>
        internal static uint Hash(uint seed, int x, int y, int salt)
        {
            unchecked
            {
                uint hash = seed ^ 0x9E3779B9u;
                hash ^= (uint)x * 0x85EBCA6Bu;
                hash = ((hash << 13) | (hash >> 19)) * 5u + 0xE6546B64u;
                hash ^= (uint)y * 0xC2B2AE35u;
                hash = ((hash << 13) | (hash >> 19)) * 5u + 0xE6546B64u;
                hash ^= (uint)salt * 0x27D4EB2Fu;

                hash ^= hash >> 16;
                hash *= 0x85EBCA6Bu;
                hash ^= hash >> 13;
                hash *= 0xC2B2AE35u;
                hash ^= hash >> 16;
                return hash;
            }
        }

        /// <summary>Get a stable hash for a location name (unlike <see cref="string.GetHashCode()"/>, it's the same every time the game runs).</summary>
        /// <param name="text">The text to hash.</param>
        internal static uint StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (char ch in text)
                {
                    hash ^= ch;
                    hash *= 16777619u;
                }
                return hash;
            }
        }

        /// <summary>Get smooth two-octave noise from 0 to 1, so cover forms natural-looking patches instead of random speckles.</summary>
        /// <param name="seed">The location seed.</param>
        /// <param name="x">The tile X position.</param>
        /// <param name="y">The tile Y position.</param>
        internal static float Noise(uint seed, int x, int y)
        {
            return 0.65f * ValueNoise(seed, x, y, 7f, 1) + 0.35f * ValueNoise(seed, x, y, 3f, 2);
        }

        /// <summary>Get value noise from 0 to 1 at one scale.</summary>
        internal static float ValueNoise(uint seed, int x, int y, float cellSize, int salt)
        {
            float fx = x / cellSize;
            float fy = y / cellSize;
            int x0 = (int)MathF.Floor(fx);
            int y0 = (int)MathF.Floor(fy);
            float tx = Smooth(fx - x0);
            float ty = Smooth(fy - y0);

            float topLeft = ToUnit(Hash(seed, x0, y0, salt));
            float topRight = ToUnit(Hash(seed, x0 + 1, y0, salt));
            float bottomLeft = ToUnit(Hash(seed, x0, y0 + 1, salt));
            float bottomRight = ToUnit(Hash(seed, x0 + 1, y0 + 1, salt));

            float top = topLeft + (topRight - topLeft) * tx;
            float bottom = bottomLeft + (bottomRight - bottomLeft) * tx;
            return top + (bottom - top) * ty;

            static float Smooth(float t) => t * t * (3f - 2f * t);
        }

        /// <summary>Convert a hash to a number from 0 to 1.</summary>
        internal static float ToUnit(uint hash)
        {
            return (hash >> 8) * (1f / 16777216f);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Draw the cover on every visible tile.</summary>
        private static void DrawTiles(SpriteBatch spriteBatch, Texture2D texture, ScreenState state, GroundCache cache, GameLocation location)
        {
            xTile.Dimensions.Rectangle viewport = Game1.viewport;
            int minX = Math.Max(0, viewport.X / TileSize - 1);
            int minY = Math.Max(0, viewport.Y / TileSize - 1);
            int maxX = Math.Min(cache.Width - 1, (viewport.X + viewport.Width) / TileSize + 1);
            int maxY = Math.Min(cache.Height - 1, (viewport.Y + viewport.Height) / TileSize + 1);

            GroundCoverKind kind = state.Kind;
            float coverage = state.Coverage;
            bool growing = kind == GroundCoverKind.SummerFlowers;
            byte neededFlag = growing ? GrowingFlag : LyingFlag;
            byte[] leafSprites = state.LeafPalette == LeafPalette.Autumn ? AutumnLeafSprites : LastLeafSprites;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int index = y * cache.Width + x;
                    float threshold = cache.Thresholds[index];
                    if (threshold >= coverage || (cache.Flags[index] & neededFlag) == 0)
                        continue;

                    // keep tilled soil visible (you need to see whether it's watered), and don't grow plants on paths
                    if (location.terrainFeatures.TryGetValue(new Vector2(x, y), out TerrainFeature? feature) && (feature is HoeDirt || (growing && feature is Flooring)))
                        continue;

                    float margin = coverage - threshold;
                    Color color = Color.White * Math.Min(1f, margin / FadeBand);
                    Vector2 tile = new(x * TileSize - viewport.X, y * TileSize - viewport.Y);
                    uint hash = Hash(cache.Seed, x, y, 0);

                    switch (kind)
                    {
                        case GroundCoverKind.FallenLeaves:
                            DrawScattered(spriteBatch, texture, cache, x, y, tile, color, leafSprites, GetScatterCount(margin, 4));
                            break;

                        case GroundCoverKind.FallenPetals:
                            DrawScattered(spriteBatch, texture, cache, x, y, tile, color, PetalSprites, GetScatterCount(margin, 3));
                            break;

                        default:
                            {
                                byte cell = hash % 3 == 0 ? SpriteFactory.GroundGrassTuft : SpriteFactory.GroundSummerFlowers;
                                spriteBatch.Draw(texture, tile + Jitter(hash, 4, 2), SpriteFactory.Ground.GetSourceRect(cell), color, 0f, Vector2.Zero, Game1.pixelZoom, FlipH(hash, 0), 0f);
                                break;
                            }
                    }
                }
            }
        }

        /// <summary>Draw a few fallen leaves or petals at stable random spots on a tile.</summary>
        private static void DrawScattered(SpriteBatch spriteBatch, Texture2D texture, GroundCache cache, int x, int y, Vector2 tile, Color color, byte[] sprites, int count)
        {
            for (int i = 0; i < count; i++)
            {
                uint hash = Hash(cache.Seed, x, y, i + 1);
                byte sprite = sprites[hash % (uint)sprites.Length];

                // the sprite is 32 pixels wide at 4x, so centres between 16 and 48 keep it inside the 64-pixel tile
                Vector2 offset = new(16 + (int)((hash >> 4) % 9) * Game1.pixelZoom, 16 + (int)((hash >> 8) % 9) * Game1.pixelZoom);
                float rotation = QuarterTurns[(hash >> 12) & 3];
                SpriteEffects effects = ((hash >> 14) & 1) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;

                spriteBatch.Draw(texture, tile + offset, SpriteFactory.Particles.GetSourceRect(sprite), color, rotation, ParticleOrigin, Game1.pixelZoom, effects, 0f);
            }
        }

        /// <summary>Get a stable random offset of up to <paramref name="range"/> game pixels in each direction.</summary>
        /// <param name="hash">The tile hash.</param>
        /// <param name="shift">Which hash bits to use.</param>
        /// <param name="range">The largest offset in game pixels (each is 4 screen pixels).</param>
        private static Vector2 Jitter(uint hash, int shift, int range)
        {
            uint span = (uint)(range * 2 + 1);
            int offsetX = (int)((hash >> shift) % span) - range;
            int offsetY = (int)((hash >> (shift + 5)) % span) - range;
            return new Vector2(offsetX * Game1.pixelZoom, offsetY * Game1.pixelZoom);
        }

        /// <summary>Get a stable random horizontal flip.</summary>
        private static SpriteEffects FlipH(uint hash, int bit)
        {
            return ((hash >> (24 + bit)) & 1) != 0 ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        }

        /// <summary>Get the cached tile data for a location, building it if needed (once per location per day, not every frame).</summary>
        private GroundCache? GetCache(ScreenState state, GameLocation location)
        {
            Map? map = location.Map;
            if (map is null)
                return null;

            int dateKey = Game1.year * WorldDate.DaysPerYear + (int)Game1.season * WorldDate.DaysPerMonth + Game1.dayOfMonth;

            GroundCache? cache = state.Cache;
            if (cache != null && cache.IsFor(location, map, dateKey))
                return cache;

            string key = location.NameOrUniqueName ?? string.Empty;
            if (!state.Caches.TryGetValue(key, out cache) || !cache.IsFor(location, map, dateKey))
            {
                if (state.Caches.Count >= MaxCachedLocations)
                    state.Caches.Clear();

                cache = GroundCache.Build(location, map, dateKey);
                state.Caches[key] = cache;
            }

            state.Cache = cache;
            return cache;
        }


        /*********
        ** Private types
        *********/
        /// <summary>Which tiles in a location can get cover, and in what order they get it.</summary>
        private sealed class GroundCache
        {
            /// <summary>The location this data is for.</summary>
            public GameLocation Location { get; }

            /// <summary>The map this data was built from (it's rebuilt if the map is replaced).</summary>
            public Map Map { get; }

            /// <summary>The date this data was built on (it's rebuilt daily, since mods can edit maps).</summary>
            public int DateKey { get; }

            /// <summary>The location's hash seed.</summary>
            public uint Seed { get; }

            /// <summary>The map width in tiles.</summary>
            public int Width { get; }

            /// <summary>The map height in tiles.</summary>
            public int Height { get; }

            /// <summary>For each tile, the coverage at which it gets cover (spread evenly from 0 to 1, so coverage 0.3 covers 30% of the open ground), or <see cref="Ineligible"/>.</summary>
            public float[] Thresholds { get; }

            /// <summary>For each tile, what kind of cover it can have (<see cref="LyingFlag"/> and <see cref="GrowingFlag"/>).</summary>
            public byte[] Flags { get; }

            /// <summary>Construct an instance.</summary>
            private GroundCache(GameLocation location, Map map, int dateKey, uint seed, int width, int height, float[] thresholds, byte[] flags)
            {
                this.Location = location;
                this.Map = map;
                this.DateKey = dateKey;
                this.Seed = seed;
                this.Width = width;
                this.Height = height;
                this.Thresholds = thresholds;
                this.Flags = flags;
            }

            /// <summary>Get whether this data is still valid for a location.</summary>
            public bool IsFor(GameLocation location, Map map, int dateKey)
            {
                return ReferenceEquals(this.Location, location) && ReferenceEquals(this.Map, map) && this.DateKey == dateKey;
            }

            /// <summary>Scan a location's map for open ground.</summary>
            public static GroundCache Build(GameLocation location, Map map, int dateKey)
            {
                uint seed = StableHash(location.NameOrUniqueName ?? string.Empty);
                Layer? back = map.GetLayer("Back");
                Layer? buildings = map.GetLayer("Buildings");
                int width = back?.LayerWidth ?? 0;
                int height = back?.LayerHeight ?? 0;

                float[] thresholds = new float[width * height];
                byte[] flags = new byte[width * height];
                Array.Fill(thresholds, Ineligible);
                if (back is null)
                    return new GroundCache(location, map, dateKey, seed, width, height, thresholds, flags);

                // find the open ground: a ground tile, not water, and nothing on the Buildings layer (cliffs, walls, fences, etc.)
                float[] order = new float[width * height];
                int[] indexes = new int[width * height];
                int count = 0;
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (back.Tiles[x, y] is null || buildings?.Tiles[x, y] != null || location.isWaterTile(x, y))
                            continue;

                        // only real ground: grass, dirt or stone paths (not wooden stairs or bridges, unmarked dark gaps, or tiles marked impassable)
                        string? type = location.doesTileHaveProperty(x, y, "Type", "Back");
                        if (type is not ("Grass" or "Dirt" or "Stone") || location.doesTileHaveProperty(x, y, "Passable", "Back") != null)
                            continue;

                        // things lie on any ground, but flowers and grass tufts only grow in grass (not on paths, fields or beach sand, which maps mark as dirt)
                        int index = y * width + x;
                        flags[index] = (byte)(LyingFlag | (type is "Grass" ? GrowingFlag : 0));

                        // mostly smooth noise (patches), plus some per-tile variation (ragged edges)
                        order[count] = 0.72f * Noise(seed, x, y) + 0.28f * ToUnit(Hash(seed, x, y, 99));
                        indexes[count] = index;
                        count++;
                    }
                }

                // spread the thresholds evenly, so coverage 0.3 means 30% of the open ground
                Array.Sort(order, indexes, 0, count);
                for (int i = 0; i < count; i++)
                    thresholds[indexes[i]] = (i + 0.5f) / count;

                return new GroundCache(location, map, dateKey, seed, width, height, thresholds, flags);
            }
        }

        /// <summary>The ground cover state for one split-screen player.</summary>
        private sealed class ScreenState
        {
            /// <summary>The <see cref="GroundLayer.Generation"/> this state was last reset for.</summary>
            public int Generation = -1;

            /// <summary>The location drawn last frame.</summary>
            public GameLocation? Location;

            /// <summary>What's being drawn.</summary>
            public GroundCoverKind Kind;

            /// <summary>The coverage being drawn (follows the target over a few seconds).</summary>
            public float Coverage;

            /// <summary>Which leaf colours are being drawn.</summary>
            public LeafPalette LeafPalette;

            /// <summary>The ground cover being aimed for.</summary>
            public GroundCoverTarget Target;

            /// <summary>The tile data for the current location.</summary>
            public GroundCache? Cache;

            /// <summary>The tile data for recently visited locations, by name.</summary>
            public readonly Dictionary<string, GroundCache> Caches = new();

            /// <summary>Forget everything.</summary>
            public void Reset()
            {
                this.Location = null;
                this.Kind = GroundCoverKind.None;
                this.Coverage = 0f;
                this.Target = GroundCoverTarget.None;
                this.Cache = null;
                this.Caches.Clear();
            }
        }
    }
}
