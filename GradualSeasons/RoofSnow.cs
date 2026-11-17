using System;
using Microsoft.Xna.Framework;

namespace GradualSeasons
{
    /// <summary>Puts snow on building roofs. The game's farm buildings (the farmhouse, cabins, barns...) look the same all year, unlike its snowy town roofs.</summary>
    /// <remarks>
    /// Where a building has a paint mask, the game marks its roof pixels lime green there, so the roof is known exactly. Buildings
    /// without one (or upgrade levels the mask doesn't cover) get a thin cap of snow along the top of the building instead. Snow
    /// keeps the roof's light and shade, mapped onto a few snow colours like the game's own snowy roofs, and settles in clumps
    /// that fill in as the amount rises (the same clumps every day, so it builds up instead of flickering).
    /// </remarks>
    internal static class RoofSnow
    {
        /*********
        ** Fields
        *********/
        /// <summary>Snow colours, from the darkest outline to the brightest highlight.</summary>
        private static readonly Color[] Palette =
        {
            new(72, 86, 140),
            new(128, 152, 204),
            new(186, 207, 239),
            new(229, 239, 252),
            new(255, 255, 255)
        };

        /// <summary>The light level (0 to 1, relative to the roof) where each snow colour after the first starts.</summary>
        private static readonly float[] Bands = { 0.1f, 0.3f, 0.56f, 0.82f };

        /// <summary>The smallest alpha for a pixel to count as part of the building.</summary>
        private const int SolidAlpha = 128;

        /// <summary>The smallest lightness (0 to 1) for a pixel to look like snow (see <see cref="GetSnowyShare"/>).</summary>
        private const float SnowyLight = 0.7f;

        /// <summary>The largest difference between a pixel's strongest and weakest colour channel for it to look like snow.</summary>
        private const float SnowySpread = 85f;

        /// <summary>The share of a roof that must already look like snow for the building to count as having its own winter look.</summary>
        /// <remarks>The game's own roofs score 0 to 7%, and a fully snowed roof 30 to 85% (its shaded parts aren't counted).</remarks>
        public const float AlreadySnowyShare = 0.3f;


        /*********
        ** Accessors
        *********/
        /// <summary>The paint mask colour for a roof pixel under snow. It still counts as roof here, but it isn't the game's exact lime green, so painting the building doesn't tint the snow.</summary>
        public static readonly Color SnowyRoofMaskColor = new(0, 254, 0, 255);


        /*********
        ** Public methods
        *********/
        /// <summary>Get whether a paint mask pixel marks a roof (the game's lime green, or <see cref="SnowyRoofMaskColor"/>).</summary>
        /// <param name="maskPixel">The paint mask pixel.</param>
        public static bool IsRoof(Color maskPixel)
        {
            return maskPixel.A >= SolidAlpha && maskPixel.G >= 200 && maskPixel.R < 64 && maskPixel.B < 64;
        }

        /// <summary>Find where snow can settle on a building texture.</summary>
        /// <param name="pixels">The building texture's pixels.</param>
        /// <param name="width">The texture width.</param>
        /// <param name="height">The texture height.</param>
        /// <param name="mask">The paint mask's pixels (the same size as the texture), or <c>null</c> if there's none.</param>
        /// <param name="sourceRect">The area the game draws for the building (its first upgrade level), or empty for the whole texture.</param>
        /// <param name="seed">The seed for the uneven edge of snow caps.</param>
        /// <param name="minMaskFit">The share of the mask's roof pixels (0 to 1) that must land on the building for the mask to be trusted.</param>
        /// <param name="mismatchedFrames">The number of frames whose mask didn't fit the building (they get a snow cap instead).</param>
        /// <returns>Whether snow can settle on each pixel.</returns>
        /// <remarks>
        /// With a mask, the texture is split into frames the size of <paramref name="sourceRect"/> (one per upgrade level): frames
        /// whose mask roof fits the building use it, and full frames without one (or whose mask doesn't fit, like when another mod
        /// changed the building's look but not its mask) get a snow cap. Without a mask, only the drawn area gets a snow cap
        /// (other parts of the texture may be animation frames, like a mill's sails).
        /// </remarks>
        public static bool[] FindRoof(Color[] pixels, int width, int height, Color[]? mask, Rectangle sourceRect, uint seed, float minMaskFit, out int mismatchedFrames)
        {
            bool[] roof = new bool[width * height];
            mismatchedFrames = 0;

            if (mask is null)
            {
                Rectangle area = sourceRect.IsEmpty ? new Rectangle(0, 0, width, height) : Rectangle.Intersect(sourceRect, new Rectangle(0, 0, width, height));
                AddCap(pixels, width, area, roof, seed);
                return roof;
            }

            int frameWidth = sourceRect.IsEmpty ? width : Math.Clamp(sourceRect.Width, 1, width);
            int frameHeight = sourceRect.IsEmpty ? height : Math.Clamp(sourceRect.Height, 1, height);
            for (int top = 0; top < height; top += frameHeight)
            {
                for (int left = 0; left < width; left += frameWidth)
                {
                    Rectangle frame = new(left, top, Math.Min(frameWidth, width - left), Math.Min(frameHeight, height - top));

                    // how much of the mask's roof lands on the building
                    int maskRoof = 0;
                    int onBuilding = 0;
                    for (int y = frame.Top; y < frame.Bottom; y++)
                    {
                        for (int x = frame.Left; x < frame.Right; x++)
                        {
                            int index = y * width + x;
                            if (IsRoof(mask[index]))
                            {
                                maskRoof++;
                                if (pixels[index].A >= SolidAlpha)
                                    onBuilding++;
                            }
                        }
                    }
                    bool fits = maskRoof > 0 && onBuilding >= maskRoof * minMaskFit;
                    if (maskRoof > 0 && !fits)
                        mismatchedFrames++;

                    if (fits)
                    {
                        for (int y = frame.Top; y < frame.Bottom; y++)
                        {
                            for (int x = frame.Left; x < frame.Right; x++)
                            {
                                int index = y * width + x;
                                roof[index] = IsRoof(mask[index]) && pixels[index].A >= SolidAlpha;
                            }
                        }
                    }
                    else if (frame.Width == frameWidth && frame.Height == frameHeight)
                        AddCap(pixels, width, frame, roof, seed);
                }
            }

            return roof;
        }

        /// <summary>Cover roof pixels with snow.</summary>
        /// <param name="pixels">The building texture's pixels (premultiplied alpha), changed in place.</param>
        /// <param name="width">The texture width.</param>
        /// <param name="roof">Where snow can settle (see <see cref="FindRoof"/>).</param>
        /// <param name="seed">The seed for the snow clumps.</param>
        /// <param name="amount">How much of the roof is covered, from 0 (none) to 1 (all of it).</param>
        /// <returns>The number of pixels changed.</returns>
        public static int Apply(Color[] pixels, int width, bool[] roof, uint seed, float amount)
        {
            if (amount <= 0f)
                return 0;

            GetLightRange(pixels, roof, out float low, out float high);
            int changed = 0;
            for (int index = 0; index < pixels.Length; index++)
            {
                Color color = pixels[index];
                if (!roof[index] || color.A == 0)
                    continue;

                float presence = TextureBlender.GetNextPresence(seed, index % width, index / width, amount);
                if (presence <= 0f)
                    continue;

                Color snow = ToSnow(color, low, high);
                Color result = presence >= 1f ? snow : Color.Lerp(color, snow, presence);
                if (result != color)
                {
                    pixels[index] = result;
                    changed++;
                }
            }

            return changed;
        }

        /// <summary>Get how much of a roof already looks like snow, from 0 to 1 (like a building texture another mod gives a winter look).</summary>
        /// <param name="pixels">The building texture's pixels (premultiplied alpha).</param>
        /// <param name="roof">Where snow would settle (see <see cref="FindRoof"/>).</param>
        /// <remarks>Snow is a pale, cool colour: light, with little colour, and no warmer than it is blue. The game's roofs are all clearly coloured.</remarks>
        public static float GetSnowyShare(Color[] pixels, bool[] roof)
        {
            int total = 0;
            int snowy = 0;
            for (int index = 0; index < pixels.Length && index < roof.Length; index++)
            {
                Color color = pixels[index];
                if (!roof[index] || color.A < SolidAlpha)
                    continue;

                total++;
                if (IsSnowColor(color))
                    snowy++;
            }

            return total > 0 ? snowy / (float)total : 0f;
        }

        /// <summary>Get whether a pixel looks like snow: light, with little colour, and no warmer than it is blue.</summary>
        /// <param name="color">The pixel (premultiplied alpha).</param>
        public static bool IsSnowColor(Color color)
        {
            if (color.A < SolidAlpha)
                return false;

            float scale = 255f / color.A; // undo premultiplied alpha
            float r = Math.Min(255f, color.R * scale);
            float g = Math.Min(255f, color.G * scale);
            float b = Math.Min(255f, color.B * scale);
            float light = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            float spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
            return light >= SnowyLight && spread <= SnowySpread && b >= r - 12f && b >= g - 12f;
        }

        /// <summary>Mark the roof pixels fully under snow in a paint mask, so painting the building doesn't tint the snow.</summary>
        /// <remarks>The soft edge of each clump stays paintable, so on a painted roof it blends into the paint colour instead of showing the unpainted roof colour.</remarks>
        /// <param name="mask">The paint mask's pixels, changed in place.</param>
        /// <param name="width">The mask width.</param>
        /// <param name="roof">Where snow settles on the building texture (see <see cref="FindRoof"/>).</param>
        /// <param name="seed">The same seed used for the building texture's snow.</param>
        /// <param name="amount">The same amount used for the building texture's snow.</param>
        /// <returns>The number of pixels changed.</returns>
        public static int MarkSnowyRoof(Color[] mask, int width, bool[] roof, uint seed, float amount)
        {
            if (amount <= 0f)
                return 0;

            int changed = 0;
            for (int index = 0; index < mask.Length; index++)
            {
                if (index >= roof.Length || !roof[index] || mask[index] == SnowyRoofMaskColor || !IsRoof(mask[index]))
                    continue;

                if (TextureBlender.GetNextPresence(seed, index % width, index / width, amount) >= 1f)
                {
                    mask[index] = SnowyRoofMaskColor;
                    changed++;
                }
            }

            return changed;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Add a thin cap of snow along the top of the building in an area, a few pixels deep, with a slightly uneven lower edge.</summary>
        private static void AddCap(Color[] pixels, int width, Rectangle area, bool[] roof, uint seed)
        {
            for (int x = area.Left; x < area.Right; x++)
            {
                int top = -1;
                for (int y = area.Top; y < area.Bottom; y++)
                {
                    if (pixels[y * width + x].A >= SolidAlpha)
                    {
                        top = y;
                        break;
                    }
                }
                if (top < 0)
                    continue;

                int depth = 3 + (int)MathF.Round(GroundLayer.ValueNoise(seed, x, 0, 5f, 41) * 3f);
                for (int y = top; y < Math.Min(area.Bottom, top + depth); y++)
                {
                    int index = y * width + x;
                    if (pixels[index].A < SolidAlpha)
                        break;
                    roof[index] = true;
                }
            }
        }

        /// <summary>Get the snow colour for a roof pixel, keeping its light and shade.</summary>
        /// <param name="color">The roof pixel (premultiplied alpha).</param>
        /// <param name="low">The roof's dark end (see <see cref="GetLightRange"/>).</param>
        /// <param name="high">The roof's light end.</param>
        private static Color ToSnow(Color color, float low, float high)
        {
            float alpha = color.A / 255f;
            float light = GetLight(color);
            float level = high > low + 0.01f ? Math.Clamp((light - low) / (high - low), 0f, 1f) : 0.75f;

            int band = 0;
            while (band < Bands.Length && level >= Bands[band])
                band++;

            Color snow = Palette[band];
            return color.A == 255 ? snow : snow * alpha;
        }

        /// <summary>Get the dark and light ends of a roof's colours (ignoring the few most extreme pixels).</summary>
        private static void GetLightRange(Color[] pixels, bool[] roof, out float low, out float high)
        {
            int[] histogram = new int[256];
            int count = 0;
            for (int index = 0; index < pixels.Length; index++)
            {
                if (!roof[index] || pixels[index].A < SolidAlpha)
                    continue;

                histogram[Math.Clamp((int)(GetLight(pixels[index]) * 255f), 0, 255)]++;
                count++;
            }

            if (count == 0)
            {
                low = 0f;
                high = 1f;
                return;
            }

            low = Percentile(histogram, count, 0.05f) / 255f;
            high = Percentile(histogram, count, 0.95f) / 255f;
        }

        /// <summary>Get the value below which a share of a histogram's entries fall.</summary>
        private static int Percentile(int[] histogram, int count, float share)
        {
            int target = (int)(count * share);
            int seen = 0;
            for (int value = 0; value < histogram.Length; value++)
            {
                seen += histogram[value];
                if (seen > target)
                    return value;
            }
            return histogram.Length - 1;
        }

        /// <summary>Get how light a pixel is, from 0 to 1 (undoing premultiplied alpha).</summary>
        private static float GetLight(Color color)
        {
            if (color.A == 0)
                return 0f;
            float light = (0.299f * color.R + 0.587f * color.G + 0.114f * color.B) / 255f;
            return Math.Min(1f, light * 255f / color.A);
        }
    }
}
