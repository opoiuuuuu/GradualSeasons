using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace GradualSeasons
{
    /// <summary>Reads the game's own images straight from its Content folder, before any mod changes them.</summary>
    /// <remarks>
    /// Comparing these with the images the game actually uses shows which pixels other mods changed. That matters when a mod
    /// changes how this season looks without changing the other season to match, like a Content Patcher edit with the
    /// <c>{{season}}</c> token (it only reaches the current season's image, until that season comes): the other season's image
    /// read today is still the plain one, so mixing it in would paint over that mod's work. See <see cref="KeptPixels"/>.
    /// </remarks>
    internal static class VanillaArt
    {
        /*********
        ** Fields
        *********/
        /// <summary>Reads the game's content files without SMAPI (so no mod's changes), or <c>null</c> if not created yet.</summary>
        private static RawContentManager? Content;

        /// <summary>Whether reading failed in a way that would fail again, so it isn't tried any more this session.</summary>
        private static bool Disabled;

        /// <summary>The images read so far, by relative path (kept until <see cref="ClearCache"/>, since other mods' art can be reloaded many times a day).</summary>
        private static readonly System.Collections.Generic.Dictionary<string, (Color[] Pixels, int Width, int Height)?> Cache = new(StringComparer.OrdinalIgnoreCase);


        /*********
        ** Public methods
        *********/
        /// <summary>Read one of the game's own images as the game ships it.</summary>
        /// <param name="assetName">The asset name, like <c>Maps\fall_town</c>.</param>
        /// <param name="monitor">Logs why the images can't be read, if that happens.</param>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <returns>The image's pixels row by row, or <c>null</c> if the game has no such image (like a mod's own tile sheet) or it can't be read.</returns>
        public static Color[]? TryLoad(string assetName, IMonitor monitor, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (Disabled || string.IsNullOrWhiteSpace(assetName))
                return null;

            try
            {
                string relativePath = assetName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                if (relativePath.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
                    relativePath = relativePath[..^4];
                if (!Cache.TryGetValue(relativePath, out var image))
                {
                    image = null;
                    string root = Constants.ContentPath;
                    if (!Path.IsPathRooted(relativePath) && !relativePath.Contains(".." + Path.DirectorySeparatorChar) && File.Exists(Path.Combine(root, relativePath + ".xnb")))
                    {
                        Content ??= new RawContentManager(Game1.content.ServiceProvider, root);
                        using Texture2D texture = Content.ReadFresh<Texture2D>(relativePath);
                        Color[] pixels = new Color[texture.Width * texture.Height];
                        texture.GetData(pixels);
                        image = (pixels, texture.Width, texture.Height);
                    }
                    Cache[relativePath] = image; // null if it's not one of the game's own images
                }

                if (image is null)
                    return null;
                width = image.Value.Width;
                height = image.Value.Height;
                return image.Value.Pixels;
            }
            catch (Exception ex)
            {
                Disabled = true;
                monitor.Log($"Couldn't read the game's own images to see which art other mods changed, so changes that other mods make to only one season's look may be painted over during a transition. Technical details:\n{ex}", LogLevel.Trace);
                return null;
            }
        }


        /// <summary>Forget the images read so far (called each day, so they don't use memory once the transition is over).</summary>
        public static void ClearCache()
        {
            Cache.Clear();
        }


        /*********
        ** Private classes
        *********/
        /// <summary>A plain content manager that reads the game's files directly, without caching them.</summary>
        private sealed class RawContentManager : ContentManager
        {
            /// <summary>Construct an instance.</summary>
            /// <param name="services">The game's services (for the graphics device).</param>
            /// <param name="root">The full path to the game's Content folder.</param>
            public RawContentManager(IServiceProvider services, string root)
                : base(services, root) { }

            /// <summary>Read an asset from its file. The caller owns the result (it isn't cached).</summary>
            /// <param name="relativePath">The path relative to the Content folder, without the <c>.xnb</c> extension.</param>
            public T ReadFresh<T>(string relativePath)
            {
                return this.ReadAsset<T>(relativePath, null);
            }
        }
    }

    /// <summary>One season's version of an image, as the game uses it (with every mod's changes) and as the game ships it.</summary>
    internal readonly struct SeasonArt
    {
        /// <summary>The image as the game uses it, row by row.</summary>
        public Color[] Pixels { get; }

        /// <summary>Its width.</summary>
        public int Width { get; }

        /// <summary>Its height.</summary>
        public int Height { get; }

        /// <summary>The game's own version, row by row.</summary>
        public Color[] Vanilla { get; }

        /// <summary>Its width.</summary>
        public int VanillaWidth { get; }

        /// <summary>Its height.</summary>
        public int VanillaHeight { get; }

        /// <summary>Construct an instance.</summary>
        public SeasonArt(Color[] pixels, int width, int height, Color[] vanilla, int vanillaWidth, int vanillaHeight)
        {
            this.Pixels = pixels;
            this.Width = width;
            this.Height = height;
            this.Vanilla = vanilla;
            this.VanillaWidth = vanillaWidth;
            this.VanillaHeight = vanillaHeight;
        }

        /// <summary>Get whether a mod changed a pixel (a pixel outside the game's image was added by a mod).</summary>
        /// <param name="x">The pixel's X position.</param>
        /// <param name="y">The pixel's Y position.</param>
        public bool IsChanged(int x, int y)
        {
            if (x >= this.Width || y >= this.Height)
                return false; // not in this image at all
            if (x >= this.VanillaWidth || y >= this.VanillaHeight)
                return this.Pixels[y * this.Width + x].A > 0;
            return !SeasonTileLayer.LooksSame(this.Pixels[y * this.Width + x], this.Vanilla[y * this.VanillaWidth + x]);
        }

        /// <summary>Get the share of a square area's pixels that a mod changed (counting the pixels that show anything).</summary>
        /// <param name="left">The area's left pixel.</param>
        /// <param name="top">The area's top pixel.</param>
        /// <param name="size">The area's width and height.</param>
        public float GetChangedShareIn(int left, int top, int size)
        {
            int bottom = Math.Min(top + size, this.Height);
            int right = Math.Min(left + size, this.Width);
            int solid = 0;
            int changed = 0;
            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    bool inVanilla = x < this.VanillaWidth && y < this.VanillaHeight;
                    if (this.Pixels[y * this.Width + x].A == 0 && (!inVanilla || this.Vanilla[y * this.VanillaWidth + x].A == 0))
                        continue;
                    solid++;
                    if (this.IsChanged(x, y))
                        changed++;
                }
            }
            return solid > 0 ? changed / (float)solid : 0f;
        }
    }

    /// <summary>The pixels of this season's image that another mod changed but the other season's image doesn't have to match, which are kept as they are during a transition.</summary>
    /// <remarks>
    /// For example, a mod that gives the special order board its own look each season with a Content Patcher <c>{{season}}</c>
    /// edit only changes the current season's tile sheet; the next season's version read today is still the game's own, so its
    /// board would be painted over the mod's one. A recolour that covers every season's images changes the other seasons too,
    /// so nothing is kept and it changes gradually in its own colours. Retextures that redraw whole buildings for the current
    /// season only (like Way Back Pelican Town) are kept too, even alongside a recolour; see <see cref="Find"/>.
    /// </remarks>
    internal sealed class KeptPixels
    {
        /*********
        ** Fields
        *********/
        /// <summary>The size of one tile, in pixels (each tile-sized area of the image is checked on its own).</summary>
        private const int TileSize = 16;

        /// <summary>The share of a tile-sized area that must be changed in every remaining season for the change to count as covering every season, like a recolour (which changes most of each tile, over 70% in tests). Added details like a few fallen leaves on a roof change much less.</summary>
        internal const float BroadlyChangedShare = 0.5f;

        /// <summary>The share of a group of touching kept areas that must be snow or ice the other season doesn't have for the whole group to follow the season after all (see <see cref="Find"/>).</summary>
        internal const float SeasonalSnowShare = 0.5f;

        /// <summary>The share of a map tile's visible pixels that must not follow a colour swap of the game's own art there for the tile to count as redrawn (see <see cref="GetRedrawnShare"/>). A recolour that only swaps colours scores 0. In tests, every tile Way Back Pelican Town left alone scored under 2%, with or without a recolour, and nearly all the tiles it redrew scored well over this.</summary>
        internal const float RedrawnShare = 0.15f;

        /// <summary>The share every other season's version of a map tile must score under (see <see cref="GetRedrawnShare"/>) for the tile to count as redrawn for this season only. A recolour painted by hand (with its own shading or texture) scores about the same in every season, and a tile every season redrew (like an expansion's own art) too, so those still change gradually.</summary>
        internal const float UntouchedShare = 0.05f;

        /// <summary>The fewest visible pixels a version of a map tile needs for its score to count (see <see cref="GetRedrawnShare"/>). A tile that's nearly empty in a season can't show how a recolour treats it.</summary>
        internal const int MinVisiblePixels = 32;

        /// <summary>The most map tiles in a group of tiles closed in by kept tiles that are kept along with them (see <see cref="KeepEnclosedTiles"/>). A bigger area is more likely a separate piece of art.</summary>
        internal const int MaxEnclosedTiles = 12;

        /// <summary>Whether each pixel is kept, row by row.</summary>
        private readonly bool[] Keep;

        /// <summary>The share of each tile's pixels that are kept, by the tile's top-left pixel index (filled as tiles are asked for).</summary>
        private readonly System.Collections.Generic.Dictionary<int, float> TileShares = new();


        /*********
        ** Accessors
        *********/
        /// <summary>Whether each pixel is kept, row by row (the same layout as the image).</summary>
        public bool[] Pixels => this.Keep;

        /// <summary>The image width.</summary>
        public int Width { get; }

        /// <summary>The image height.</summary>
        public int Height { get; }

        /// <summary>How many pixels are kept.</summary>
        public int Count { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Find which pixels to keep. Pixels are matched by position, like the seasonal versions of a texture (which can differ in size: the winter oak is narrower).</summary>
        /// <param name="current">This season's version.</param>
        /// <param name="other">The other season's version (the one being mixed in), as the game gives it today.</param>
        /// <param name="rest">The remaining seasons' versions, where they exist.</param>
        /// <param name="mapTiles">Whether this is a map tile sheet (the game draws each 16x16 tile of it on the map on its own). Then a tile another mod redrew for this season only is kept whole, and so are tiles closed in by kept tiles; see the remarks.</param>
        /// <returns>The pixels to keep (laid out like <paramref name="current"/>), or <c>null</c> if there are none.</returns>
        /// <remarks>
        /// A pixel is kept where a mod changed this season's art but the other season's is still the game's own. Each tile-sized
        /// area is then checked as a whole: if every remaining season is broadly changed there too, the change covers every season
        /// (like a recolour, which can leave a few colours of one season, such as white snow, exactly as they were), so it's
        /// followed as usual. That's decided area by area, because expansions like SVE change parts of every season's tile sheets
        /// while a seasonal special order board only changes one spot of the current season's; and only a broad change counts, so
        /// small details another mod adds to one season (like a few fallen leaves on a roof) don't split a roof into squares.
        ///
        /// Snow and ice are different: mods that give buildings a winter look (like Elle's Seasonal Vanilla Buildings, or SVE's
        /// snow on buildings and objects) add them to the winter tile sheets for good, so they should melt and settle like the
        /// game's own snow. Touching areas are grouped (a roof with its eaves, a whole frozen fountain), and a group that's mostly
        /// snow or ice the other season doesn't have follows the season as a whole, so nothing is left behind as a tile-sized block.
        ///
        /// Map tile sheets get two more checks, for retextures that redraw whole buildings for the current season only (like Way
        /// Back Pelican Town, which edits the tile sheets with the <c>{{season}}</c> token). With a recolour that covers every
        /// season installed too, the other season's tile sheet isn't the game's own any more, so the check above can't see the
        /// redrawn art at all; and even without one, wherever the redrawn art happens to match the game's (like a patch of snow on
        /// a redrawn roof), the game's own version of the other season would show through in patches. So each tile is also
        /// checked against the game's own art in a way a recolour can't hide (see <see cref="GetRedrawnShare"/>): a tile this
        /// season redrew but no other season did is kept whole (a recolour painted by hand treats every season alike, so its
        /// own shading doesn't count). Then tiles that would change but are closed in by kept tiles
        /// (like a flat snowy part of a redrawn roof) are kept too (see <see cref="KeepEnclosedTiles"/>). The snow check above
        /// still applies, so Elle's or SVE's snow on the game's own buildings melts as before.
        /// </remarks>
        public static KeptPixels? Find(SeasonArt current, SeasonArt other, System.Collections.Generic.IReadOnlyList<SeasonArt> rest, bool mapTiles = false)
        {
            bool[]? keep = null;
            int rows = Math.Min(Math.Min(current.Height, current.VanillaHeight), other.Height);
            int columns = Math.Min(Math.Min(current.Width, current.VanillaWidth), other.Width);
            int blockColumns = (columns + TileSize - 1) / TileSize;
            int blockRows = (rows + TileSize - 1) / TileSize;
            bool[] keptBlock = new bool[blockColumns * blockRows];
            int[] keptInBlock = new int[blockColumns * blockRows];
            int[] snowInBlock = new int[blockColumns * blockRows];
            int[] candidates = new int[TileSize * TileSize];
            TileScratch? scratch = mapTiles ? new TileScratch() : null;
            for (int top = 0; top < rows; top += TileSize)
            {
                for (int left = 0; left < columns; left += TileSize)
                {
                    int block = top / TileSize * blockColumns + left / TileSize;
                    int bottom = Math.Min(top + TileSize, rows);
                    int right = Math.Min(left + TileSize, columns);

                    // a map tile this season's art redrew but the other season's didn't is kept whole, so the game's own version
                    // of it never shows through in patches (whatever recolour is installed)
                    if (scratch != null && IsRedrawnThisSeasonOnly(current, other, rest, left, top, scratch))
                    {
                        keep ??= new bool[current.Width * current.Height];
                        int counted = 0;
                        int snowy = 0;
                        for (int y = top; y < bottom; y++)
                        {
                            for (int x = left; x < right; x++)
                            {
                                int index = y * current.Width + x;
                                keep[index] = true;

                                // for the snow check below, count what this season's art changed that looks different in the other season
                                Color now = current.Pixels[index];
                                Color then = other.Pixels[y * other.Width + x];
                                if (SeasonTileLayer.LooksSame(now, then) || !current.IsChanged(x, y))
                                    continue;
                                counted++;
                                if (LooksWintry(now) && !LooksWintry(then))
                                    snowy++;
                            }
                        }
                        keptBlock[block] = true;
                        keptInBlock[block] = counted;
                        snowInBlock[block] = snowy;
                        continue;
                    }

                    // pixels a mod changed this season but not in the other season
                    int found = 0;
                    for (int y = top; y < bottom; y++)
                    {
                        for (int x = left; x < right; x++)
                        {
                            if (current.IsChanged(x, y) && !other.IsChanged(x, y))
                                candidates[found++] = y * current.Width + x;
                        }
                    }
                    if (found == 0)
                        continue;

                    // if every remaining season is broadly changed here too, the change covers every season (like a recolour), so
                    // it's followed as usual
                    bool everySeason = rest.Count > 0;
                    foreach (SeasonArt season in rest)
                    {
                        if (season.GetChangedShareIn(left, top, TileSize) < BroadlyChangedShare)
                        {
                            everySeason = false;
                            break;
                        }
                    }
                    if (everySeason)
                        continue;

                    keep ??= new bool[current.Width * current.Height];
                    int snow = 0;
                    for (int i = 0; i < found; i++)
                    {
                        int index = candidates[i];
                        keep[index] = true;
                        int x = index % current.Width;
                        int y = index / current.Width;
                        if (LooksWintry(current.Pixels[index]) && !LooksWintry(other.Pixels[y * other.Width + x]))
                            snow++;
                    }
                    keptBlock[block] = true;
                    keptInBlock[block] = found;
                    snowInBlock[block] = snow;
                }
            }
            if (keep is null)
                return null;

            // groups of touching areas that are mostly snow or ice follow the season after all
            bool[] seen = new bool[keptBlock.Length];
            System.Collections.Generic.List<int> group = new();
            System.Collections.Generic.Stack<int> pending = new();
            for (int start = 0; start < keptBlock.Length; start++)
            {
                if (!keptBlock[start] || seen[start])
                    continue;

                group.Clear();
                seen[start] = true;
                pending.Push(start);
                int groupKept = 0;
                int groupSnow = 0;
                while (pending.Count > 0)
                {
                    int block = pending.Pop();
                    group.Add(block);
                    groupKept += keptInBlock[block];
                    groupSnow += snowInBlock[block];
                    int blockX = block % blockColumns;
                    int blockY = block / blockColumns;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = blockX + dx;
                            int ny = blockY + dy;
                            if (nx < 0 || ny < 0 || nx >= blockColumns || ny >= blockRows)
                                continue;
                            int neighbour = ny * blockColumns + nx;
                            if (keptBlock[neighbour] && !seen[neighbour])
                            {
                                seen[neighbour] = true;
                                pending.Push(neighbour);
                            }
                        }
                    }
                }

                if (groupKept == 0 || groupSnow < groupKept * SeasonalSnowShare)
                    continue;
                foreach (int block in group)
                {
                    int left = block % blockColumns * TileSize;
                    int top = block / blockColumns * TileSize;
                    for (int y = top; y < Math.Min(top + TileSize, rows); y++)
                        Array.Clear(keep, y * current.Width + left, Math.Min(TileSize, columns - left));
                }
            }

            // on a map tile sheet, tiles closed in by kept tiles are kept too
            if (mapTiles)
                KeepEnclosedTiles(keep, current, other);

            int count = 0;
            foreach (bool kept in keep)
            {
                if (kept)
                    count++;
            }
            return count > 0 ? new KeptPixels(keep, current.Width, current.Height, count) : null;
        }

        /// <summary>Get how much of a 16x16 tile doesn't follow a colour swap of the game's own art there: the share of its visible pixels whose colour isn't the most common one among the pixels that have the same colour in the game's version.</summary>
        /// <param name="art">The image and the game's own version of it.</param>
        /// <param name="left">The tile's left pixel.</param>
        /// <param name="top">The tile's top pixel.</param>
        /// <param name="scratch">Reusable buffers.</param>
        /// <param name="visible">The number of pixels that show anything in either version (only those are scored).</param>
        /// <returns>0 if the tile is the game's own art or a recolour of it that only swaps colours (it gives every pixel of one of the game's colours the same new colour, whatever its palette), up to nearly 1 for a tile drawn completely differently.</returns>
        /// <remarks>The tile must lie inside both images.</remarks>
        internal static float GetRedrawnShare(SeasonArt art, int left, int top, TileScratch scratch, out int visible)
        {
            // quick check first: if every pixel of each of the game's colours has exactly one colour here (the game's own art or
            // a plain colour swap, which is most tiles), nothing was redrawn
            if (IsColourSwap(art, left, top, scratch, out visible))
                return 0f;

            // pair each pixel's colour in the game's version with its colour here, sorted so the pixels of each of the game's colours are together
            ulong[] colours = scratch.Colours;
            int count = 0;
            for (int y = top; y < top + TileSize; y++)
            {
                for (int x = left; x < left + TileSize; x++)
                    colours[count++] = (ulong)art.Vanilla[y * art.VanillaWidth + x].PackedValue << 32 | art.Pixels[y * art.Width + x].PackedValue;
            }
            Array.Sort(colours, 0, count);

            // count the visible pixels that match the most common new colour of their old colour
            int matching = 0;
            visible = 0;
            for (int start = 0; start < count;)
            {
                ulong original = colours[start] >> 32;
                int end = start;
                ulong usual = colours[start];
                int usualCount = 0;
                while (end < count && colours[end] >> 32 == original)
                {
                    int run = end;
                    while (end < count && colours[end] == colours[run])
                        end++;
                    if (end - run > usualCount)
                    {
                        usualCount = end - run;
                        usual = colours[run];
                    }
                }

                Color before = new((uint)original);
                Color usualColour = new((uint)usual);
                for (int i = start; i < end; i++)
                {
                    Color now = new((uint)colours[i]);
                    if (before.A == 0 && now.A == 0)
                        continue; // empty in both versions
                    visible++;
                    if (SeasonTileLayer.LooksSame(now, usualColour))
                        matching++;
                }
                start = end;
            }

            return visible > 0 ? 1f - matching / (float)visible : 0f;
        }

        /// <summary>Get whether a 16x16 tile is exactly a colour swap of the game's own art there: every visible pixel of one of the game's colours has the same colour here.</summary>
        /// <param name="art">The image and the game's own version of it.</param>
        /// <param name="left">The tile's left pixel.</param>
        /// <param name="top">The tile's top pixel.</param>
        /// <param name="scratch">Reusable buffers.</param>
        /// <param name="visible">The number of pixels that show anything in either version (counted fully only if this returns true).</param>
        private static bool IsColourSwap(SeasonArt art, int left, int top, TileScratch scratch, out int visible)
        {
            visible = 0;
            int stamp = scratch.NextStamp();
            for (int y = top; y < top + TileSize; y++)
            {
                for (int x = left; x < left + TileSize; x++)
                {
                    uint before = art.Vanilla[y * art.VanillaWidth + x].PackedValue;
                    uint now = art.Pixels[y * art.Width + x].PackedValue;
                    if (before == 0 && now == 0)
                        continue; // empty in both versions
                    visible++;

                    // look up the colour this pixel's original colour got so far (a small hash table, cleared by bumping the stamp)
                    int slot = (int)((before * 2654435761u) >> 23); // 9 bits: 0 to 511
                    while (true)
                    {
                        if (scratch.Stamps[slot] != stamp)
                        {
                            scratch.Stamps[slot] = stamp;
                            scratch.Originals[slot] = before;
                            scratch.Swapped[slot] = now;
                            break;
                        }
                        if (scratch.Originals[slot] == before)
                        {
                            if (scratch.Swapped[slot] != now)
                                return false;
                            break;
                        }
                        slot = (slot + 1) & (TileScratch.Slots - 1);
                    }
                }
            }
            return true;
        }

        /// <summary>Get whether this season's art redrew a 16x16 tile that no other season's art redrew (see <see cref="GetRedrawnShare"/>).</summary>
        /// <param name="current">This season's version.</param>
        /// <param name="other">The other season's version.</param>
        /// <param name="rest">The remaining seasons' versions.</param>
        /// <param name="left">The tile's left pixel.</param>
        /// <param name="top">The tile's top pixel.</param>
        /// <param name="scratch">Reusable buffers.</param>
        /// <remarks>
        /// Every other season with something to show in the tile counts as a witness, and all of them must look untouched: a
        /// recolour painted by hand (with its own shading or texture) treats every season alike, so it never counts, while a
        /// retexture that only applies to the current season (like Way Back Pelican Town) does. A tile that's (nearly) empty in
        /// every other season can't be judged, so it isn't counted either.
        /// </remarks>
        private static bool IsRedrawnThisSeasonOnly(SeasonArt current, SeasonArt other, System.Collections.Generic.IReadOnlyList<SeasonArt> rest, int left, int top, TileScratch scratch)
        {
            if (!ContainsTile(current, left, top) || !ContainsTile(other, left, top))
                return false; // only whole tiles of the game's own images
            if (GetRedrawnShare(current, left, top, scratch, out int visible) < RedrawnShare || visible < MinVisiblePixels)
                return false;

            bool witnessed = false;
            for (int i = -1; i < rest.Count; i++)
            {
                SeasonArt season = i < 0 ? other : rest[i];
                if (!ContainsTile(season, left, top))
                    continue;
                float share = GetRedrawnShare(season, left, top, scratch, out int seen);
                if (seen < MinVisiblePixels)
                    continue;
                if (share >= UntouchedShare)
                    return false;
                witnessed = true;
            }
            return witnessed;
        }

        /// <summary>Get whether a whole 16x16 tile lies inside an image and the game's own version of it.</summary>
        /// <param name="art">The image and the game's own version of it.</param>
        /// <param name="left">The tile's left pixel.</param>
        /// <param name="top">The tile's top pixel.</param>
        private static bool ContainsTile(SeasonArt art, int left, int top)
        {
            return left >= 0 && top >= 0
                && left + TileSize <= Math.Min(art.Width, art.VanillaWidth)
                && top + TileSize <= Math.Min(art.Height, art.VanillaHeight)
                && art.Pixels != null && art.Vanilla != null;
        }

        /// <summary>On a map tile sheet, keep the tiles that would change but are closed in by kept tiles, like a flat snowy part of a redrawn roof that happens to match the game's own art.</summary>
        /// <param name="keep">Which pixels are kept so far, updated in place.</param>
        /// <param name="current">This season's version.</param>
        /// <param name="other">The other season's version.</param>
        /// <remarks>
        /// A group of touching tiles that would change (and aren't mostly kept) is kept whole if it's small, doesn't reach the edge
        /// of the image, and the tiles around it are kept, or at least twice as many are kept as not. So a part of a redrawn building
        /// doesn't turn on its own, while the game's own art next to a kept area (a tree with empty space around it, say) still does.
        /// </remarks>
        private static void KeepEnclosedTiles(bool[] keep, SeasonArt current, SeasonArt other)
        {
            int tileColumns = Math.Min(current.Width, other.Width) / TileSize;
            int tileRows = Math.Min(current.Height, other.Height) / TileSize;
            if (tileColumns == 0 || tileRows == 0)
                return;

            // find which tiles are mostly kept (the same test the map uses), and which would change otherwise
            bool[] keptTile = new bool[tileColumns * tileRows];
            bool[] open = new bool[tileColumns * tileRows];
            for (int tile = 0; tile < keptTile.Length; tile++)
            {
                int left = tile % tileColumns * TileSize;
                int top = tile / tileColumns * TileSize;
                int solid = 0;
                int kept = 0;
                bool changes = false;
                for (int y = top; y < top + TileSize; y++)
                {
                    for (int x = left; x < left + TileSize; x++)
                    {
                        int index = y * current.Width + x;
                        Color now = current.Pixels[index];
                        if (keep[index])
                        {
                            kept++;
                            solid++;
                        }
                        else if (now.A != 0)
                            solid++;
                        if (!changes && !SeasonTileLayer.LooksSame(now, other.Pixels[y * other.Width + x]))
                            changes = true;
                    }
                }
                keptTile[tile] = solid > 0 && kept >= solid * SeasonTileLayer.MostlyKeptShare;
                open[tile] = changes && !keptTile[tile];
            }

            // keep the small groups of open tiles closed in by kept ones
            bool[] seen = new bool[open.Length];
            System.Collections.Generic.List<int> group = new();
            System.Collections.Generic.HashSet<int> around = new();
            System.Collections.Generic.Stack<int> pending = new();
            for (int start = 0; start < open.Length; start++)
            {
                if (!open[start] || seen[start])
                    continue;

                group.Clear();
                around.Clear();
                bool atEdge = false;
                seen[start] = true;
                pending.Push(start);
                while (pending.Count > 0)
                {
                    int tile = pending.Pop();
                    group.Add(tile);
                    int tileX = tile % tileColumns;
                    int tileY = tile / tileColumns;
                    for (int side = 0; side < 4; side++)
                    {
                        int nx = tileX + (side == 0 ? -1 : side == 1 ? 1 : 0);
                        int ny = tileY + (side == 2 ? -1 : side == 3 ? 1 : 0);
                        if (nx < 0 || ny < 0 || nx >= tileColumns || ny >= tileRows)
                        {
                            atEdge = true;
                            continue;
                        }

                        int neighbour = ny * tileColumns + nx;
                        if (!open[neighbour])
                            around.Add(neighbour);
                        else if (!seen[neighbour])
                        {
                            seen[neighbour] = true;
                            pending.Push(neighbour);
                        }
                    }
                }

                if (atEdge || group.Count > MaxEnclosedTiles)
                    continue;
                int keptAround = 0;
                foreach (int tile in around)
                {
                    if (keptTile[tile])
                        keptAround++;
                }
                if (keptAround == 0 || keptAround < (around.Count - keptAround) * 2)
                    continue;

                foreach (int tile in group)
                {
                    int left = tile % tileColumns * TileSize;
                    int top = tile / tileColumns * TileSize;
                    for (int y = top; y < top + TileSize; y++)
                        Array.Fill(keep, true, y * current.Width + left, TileSize);
                }
            }
        }

        /// <summary>Get whether a pixel looks like snow or ice (white, pale grey or pale blue).</summary>
        /// <param name="color">The pixel colour (premultiplied).</param>
        internal static bool LooksWintry(Color color)
        {
            if (color.A < 128)
                return false;

            float scale = 255f / color.A; // undo premultiplied alpha
            float r = Math.Min(255f, color.R * scale);
            float g = Math.Min(255f, color.G * scale);
            float b = Math.Min(255f, color.B * scale);
            float light = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            float spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            return light >= 0.7f && spread <= 110f && b >= r - 12f && b >= g - 12f;
        }

        /// <summary>Get the share of a tile's pixels that are kept (counting the pixels that show anything, plus any kept transparent ones).</summary>
        /// <param name="x">The tile's left pixel in the image.</param>
        /// <param name="y">The tile's top pixel in the image.</param>
        /// <param name="current">This season's image pixels.</param>
        public float GetTileShare(int x, int y, Color[] current)
        {
            if (x < 0 || y < 0 || x + TileSize > this.Width || y + TileSize > this.Height)
                return 0f;

            int key = y * this.Width + x;
            if (this.TileShares.TryGetValue(key, out float share))
                return share;

            int solid = 0;
            int kept = 0;
            for (int row = 0; row < TileSize; row++)
            {
                for (int column = 0; column < TileSize; column++)
                {
                    int index = (y + row) * this.Width + x + column;
                    bool keep = this.Keep[index];
                    if (!keep && current[index].A == 0)
                        continue;
                    solid++;
                    if (keep)
                        kept++;
                }
            }

            share = solid > 0 ? kept / (float)solid : 0f;
            this.TileShares[key] = share;
            return share;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Construct an instance.</summary>
        private KeptPixels(bool[] keep, int width, int height, int count)
        {
            this.Keep = keep;
            this.Width = width;
            this.Height = height;
            this.Count = count;
        }


        /*********
        ** Classes
        *********/
        /// <summary>Reusable buffers for checking map tiles (see <see cref="GetRedrawnShare"/>), so checking a whole tile sheet doesn't allocate per tile.</summary>
        internal sealed class TileScratch
        {
            /// <summary>The number of slots in the colour table (a power of two, at least twice the pixels in a tile).</summary>
            public const int Slots = 512;

            /// <summary>Each pixel's colour in the game's version and here, for sorting.</summary>
            public readonly ulong[] Colours = new ulong[TileSize * TileSize];

            /// <summary>The colour table: the game's colour in each used slot.</summary>
            public readonly uint[] Originals = new uint[Slots];

            /// <summary>The colour table: the colour it got here.</summary>
            public readonly uint[] Swapped = new uint[Slots];

            /// <summary>The colour table: which check each slot was last used by (a slot from an earlier check counts as empty).</summary>
            public readonly int[] Stamps = new int[Slots];

            /// <summary>The last check's stamp.</summary>
            private int Stamp;

            /// <summary>Start a new check, so every slot of the colour table counts as empty.</summary>
            public int NextStamp()
            {
                if (++this.Stamp == int.MaxValue)
                {
                    Array.Clear(this.Stamps);
                    this.Stamp = 1;
                }
                return this.Stamp;
            }
        }
    }
}
