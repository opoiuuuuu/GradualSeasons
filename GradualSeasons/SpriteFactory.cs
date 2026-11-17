using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace GradualSeasons
{
    /// <summary>One sprite sheet: a single row of square cells whose pixel art is defined as text grids.</summary>
    internal sealed class SpriteSheet
    {
        /*********
        ** Fields
        *********/
        /// <summary>The source rectangle for each cell.</summary>
        private readonly Rectangle[] SourceRects;


        /*********
        ** Accessors
        *********/
        /// <summary>A short name for log messages, like "particle".</summary>
        public string Label { get; }

        /// <summary>The PNG path, relative to the mod folder.</summary>
        public string RelativePath { get; }

        /// <summary>The width and height of one cell, in pixels.</summary>
        public int CellSize { get; }

        /// <summary>The pixel art for each cell (one string per row, one palette character per pixel).</summary>
        public string[][] Cells { get; }

        /// <summary>The number of cells.</summary>
        public int CellCount => this.Cells.Length;

        /// <summary>The sheet width in pixels.</summary>
        public int Width => this.CellSize * this.CellCount;

        /// <summary>The sheet height in pixels.</summary>
        public int Height => this.CellSize;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="label">A short name for log messages.</param>
        /// <param name="relativePath">The PNG path, relative to the mod folder.</param>
        /// <param name="cellSize">The width and height of one cell, in pixels.</param>
        /// <param name="cells">The pixel art for each cell.</param>
        public SpriteSheet(string label, string relativePath, int cellSize, string[][] cells)
        {
            this.Label = label;
            this.RelativePath = relativePath;
            this.CellSize = cellSize;
            this.Cells = cells;

            this.SourceRects = new Rectangle[cells.Length];
            for (int i = 0; i < cells.Length; i++)
                this.SourceRects[i] = new Rectangle(i * cellSize, 0, cellSize, cellSize);
        }

        /// <summary>Get the source rectangle for a cell.</summary>
        /// <param name="cell">The cell index.</param>
        public Rectangle GetSourceRect(byte cell)
        {
            return this.SourceRects[cell < this.SourceRects.Length ? cell : 0];
        }
    }

    /// <summary>Builds, loads and saves the mod's sprite sheets (<c>assets/particles.png</c> and <c>assets/ground.png</c>).</summary>
    /// <remarks>
    /// Each sheet is one row of square cells. The art is defined below as string grids plus a shared colour palette,
    /// so the mod still works if a PNG is missing. <c>tools/generate_sprites.py</c> reads the PALETTE,
    /// PARTICLE-CELLS and GROUND-CELLS blocks of this file to generate the PNGs, so keep their format when editing them.
    /// </remarks>
    internal static class SpriteFactory
    {
        /*********
        ** Constants
        *********/
        /// <summary>The width and height of one particle cell, in pixels.</summary>
        public const int ParticleCellSize = 8;

        /// <summary>The width and height of one ground cover cell, in pixels (one map tile at 4x zoom).</summary>
        public const int GroundCellSize = 16;

        /****
        ** Particle cells (assets/particles.png)
        ****/
        /// <summary>Particle cell 0: small snowflake.</summary>
        public const byte SnowSmall = 0;

        /// <summary>Particle cell 1: medium snowflake.</summary>
        public const byte SnowMedium = 1;

        /// <summary>Particle cell 2: large snowflake.</summary>
        public const byte SnowLarge = 2;

        /// <summary>Particle cell 3: orange leaf.</summary>
        public const byte LeafOrange = 3;

        /// <summary>Particle cell 4: red leaf.</summary>
        public const byte LeafRed = 4;

        /// <summary>Particle cell 5: yellow leaf.</summary>
        public const byte LeafYellow = 5;

        /// <summary>Particle cell 6: brown leaf.</summary>
        public const byte LeafBrown = 6;

        /// <summary>Particle cell 7: light pink cherry petal.</summary>
        public const byte PetalLight = 7;

        /// <summary>Particle cell 8: deep pink cherry petal.</summary>
        public const byte PetalDeep = 8;

        /// <summary>Particle cell 9: sleet streak.</summary>
        public const byte SleetStreak = 9;

        /****
        ** Ground cells (assets/ground.png)
        ****/
        /// <summary>Ground cell 0: a tuft of green grass.</summary>
        public const byte GroundGrassTuft = 0;

        /// <summary>Ground cell 1: summer wildflowers with deep green grass.</summary>
        public const byte GroundSummerFlowers = 1;


        /*********
        ** Pixel art
        *********/
        // PALETTE-BEGIN
        /// <summary>The colour for each character used in the cell grids, as 0xRRGGBBAA (not premultiplied).</summary>
        private static readonly (char Key, uint Rgba)[] Palette =
        {
            ('.', 0x00000000), // transparent

            // snow: white with pale-blue shading
            ('W', 0xFFFFFFFF), // white
            ('v', 0xE8F2FCFF), // off-white texture
            ('b', 0xD2E6FAFF), // pale blue
            ('B', 0xA0C0E8FF), // blue shade

            // orange leaf
            ('h', 0xF8B45AFF), // highlight
            ('o', 0xE8802AFF), // fill
            ('O', 0xB0501CFF), // shade and vein

            // red leaf
            ('e', 0xF06A4CFF), // highlight
            ('r', 0xD03A2EFF), // fill
            ('R', 0x8C2028FF), // shade and vein

            // yellow leaf
            ('l', 0xFAE68EFF), // highlight
            ('y', 0xF0C846FF), // fill
            ('Y', 0xC4921EFF), // shade and vein

            // brown leaf
            ('u', 0xBE8650FF), // highlight
            ('n', 0x9A6236FF), // fill
            ('N', 0x62401EFF), // shade, vein and stems

            // cherry petals: two pinks
            ('q', 0xFFE6F0FF), // pale highlight
            ('p', 0xFFBED4FF), // light pink
            ('P', 0xEE8CB2FF), // pink shade
            ('k', 0xF4A6C6FF), // deep pink highlight
            ('m', 0xD8689AFF), // deep pink

            // sleet: thin, pale blue, semi-transparent
            ('j', 0xC8E2FF50), // faint tail
            ('i', 0xD8ECFF96), // body
            ('I', 0xEEF8FFD2), // bright head

            // grass
            ('f', 0xA4D85AFF), // light green
            ('g', 0x6CB040FF), // green
            ('G', 0x3E7C2EFF), // dark green
            ('d', 0x2A5A26FF), // deep summer green

            // thawed ground
            ('c', 0xC4D4E6FF), // wet snow at the edge of a melted patch
            ('t', 0x7A5A36FF), // wet earth
            ('T', 0x54402AFF), // dark wet earth

            // flowers
            ('s', 0xFFDA3CFF), // buttercup yellow
            ('a', 0xEE9424FF), // flower centre
            ('z', 0xD4B8F6FF), // crocus lilac
            ('x', 0x9C6CD8FF), // crocus purple
            ('X', 0x6A44A6FF)  // crocus dark purple
        };
        // PALETTE-END

        // PARTICLE-CELLS-BEGIN
        /// <summary>The particle pixel art: ten 8x8 cells, top row first.</summary>
        private static readonly string[][] ParticleCells =
        {
            // 0: small snowflake
            new[]
            {
                "........",
                "........",
                "...b....",
                "..bWb...",
                "...b....",
                "........",
                "........",
                "........"
            },

            // 1: medium snowflake
            new[]
            {
                "........",
                ".b...b..",
                "..bWb...",
                ".bWBWb..",
                "..bWb...",
                ".b...b..",
                "........",
                "........"
            },

            // 2: large snowflake
            new[]
            {
                "...W....",
                ".b.W.b..",
                "..bWb...",
                "WWWBWWW.",
                "..bWb...",
                ".b.W.b..",
                "...W....",
                "........"
            },

            // 3: orange leaf
            new[]
            {
                ".......o",
                ".....ho.",
                "...hoOo.",
                "..hoOoO.",
                "..oOoO..",
                "..OoO...",
                ".N......",
                "N......."
            },

            // 4: red leaf
            new[]
            {
                "...ee...",
                "e..rr..r",
                "rr.rr.rR",
                ".rrrRrR.",
                "rrrrRrRR",
                ".rrRRRR.",
                "...NN...",
                "...N...."
            },

            // 5: yellow leaf
            new[]
            {
                "....y...",
                "...lyy..",
                "..lyyyY.",
                "..lyYyY.",
                "..yyYyY.",
                "...yYY..",
                "....N...",
                "....N..."
            },

            // 6: brown leaf
            new[]
            {
                "........",
                "..uu....",
                ".unnN...",
                ".nnNnN..",
                "..nNnnN.",
                "...NnnN.",
                "....NNN.",
                "......N."
            },

            // 7: light pink cherry petal
            new[]
            {
                "........",
                "..pp.pp.",
                "..pqqqP.",
                "..pqqpP.",
                "...pqP..",
                "....P...",
                "........",
                "........"
            },

            // 8: deep pink cherry petal
            new[]
            {
                "........",
                "....P.P.",
                "...kPPm.",
                "..kPPPm.",
                "..PPPm..",
                "..Pmm...",
                "........",
                "........"
            },

            // 9: sleet streak
            new[]
            {
                ".......j",
                "......j.",
                "......i.",
                ".....i..",
                ".....I..",
                "....I...",
                "....I...",
                "...I...."
            }
        };
        // PARTICLE-CELLS-END

        // GROUND-CELLS-BEGIN
        /// <summary>The ground decoration pixel art: two 16x16 cells (one map tile each at 4x zoom), top row first.</summary>
        /// <remarks>Snow, thaw and each season's ground colour come from the game's own tiles (see SeasonTileLayer), so they aren't here.</remarks>
        private static readonly string[][] GroundCells =
        {
            // 0: grass tuft
            new[]
            {
                "................",
                "................",
                "................",
                "................",
                "................",
                "................",
                "......f.........",
                ".....fg...f.....",
                ".....gG..fg.....",
                "..f..gG.fgG.....",
                "..gf.gGfgG..f...",
                "..gGfGgGgG.fg...",
                "...GgGGgGGfgG...",
                "....GGdGGdGG....",
                "................",
                "................"
            },

            // 1: summer wildflowers
            new[]
            {
                "................",
                "................",
                "....s...........",
                "...sas......W...",
                "....s......WsW..",
                "....G.......W...",
                "....G...s...G...",
                "...fG..sas..G...",
                "..fgGf..sG.fG...",
                "..gGgGf.GfgG....",
                "...GdGGgGgGd....",
                "....dGdGGdG.....",
                "................",
                "................",
                "................",
                "................"
            }
        };
        // GROUND-CELLS-END


        /*********
        ** Sheets
        *********/
        /// <summary>The falling particles (snow, leaves, petals, sleet).</summary>
        public static readonly SpriteSheet Particles = new("particle", "assets/particles.png", ParticleCellSize, ParticleCells);

        /// <summary>The ground decorations (grass tufts and summer flowers).</summary>
        public static readonly SpriteSheet Ground = new("ground cover", "assets/ground.png", GroundCellSize, GroundCells);


        /*********
        ** Public methods
        *********/
        /// <summary>Load a sheet's PNG if it exists, otherwise build it from the built-in art and save it there.</summary>
        /// <param name="helper">The SMAPI mod helper.</param>
        /// <param name="monitor">The SMAPI monitor used for logging.</param>
        /// <param name="sheet">The sheet to load.</param>
        /// <returns>Returns the texture, or <c>null</c> if none could be created at all (that sheet's visuals are then disabled).</returns>
        public static Texture2D? LoadOrCreate(IModHelper helper, IMonitor monitor, SpriteSheet sheet)
        {
            string fullPath = Path.Combine(helper.DirectoryPath, sheet.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            // use the PNG if it exists
            if (File.Exists(fullPath))
            {
                try
                {
                    Texture2D loaded = helper.ModContent.Load<Texture2D>(sheet.RelativePath);
                    if (loaded.Width >= sheet.Width && loaded.Height >= sheet.Height)
                    {
                        if (loaded.Width != sheet.Width || loaded.Height != sheet.Height)
                            monitor.Log($"{sheet.RelativePath} is {loaded.Width}x{loaded.Height}; only the top-left {sheet.Width}x{sheet.Height} pixels ({sheet.CellCount} cells of {sheet.CellSize}x{sheet.CellSize}) are used.", LogLevel.Warn);
                        else
                            monitor.Log($"Loaded the {sheet.Label} sprites from {sheet.RelativePath}.", LogLevel.Trace);
                        return loaded;
                    }

                    monitor.Log($"{sheet.RelativePath} is only {loaded.Width}x{loaded.Height}, but it needs to be {sheet.Width}x{sheet.Height} ({sheet.CellCount} cells of {sheet.CellSize}x{sheet.CellSize} in one row). Using the built-in {sheet.Label} sprites instead.", LogLevel.Warn);
                }
                catch (Exception ex)
                {
                    monitor.Log($"Couldn't load {sheet.RelativePath}, so the built-in {sheet.Label} sprites are used instead. Technical details:\n{ex}", LogLevel.Warn);
                }

                // don't overwrite a file the player may be editing
                return CreateBuiltInTexture(sheet, monitor);
            }

            // otherwise use the built-in art and save it so it can be edited
            Texture2D? texture = CreateBuiltInTexture(sheet, monitor);
            try
            {
                SaveBuiltInPng(sheet, fullPath);
                monitor.Log($"{sheet.RelativePath} was missing, so the built-in {sheet.Label} sprites were saved there. You can edit that file and restart the game to change how they look.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                monitor.Log($"Couldn't save the built-in {sheet.Label} sprites to {sheet.RelativePath} (they're still used). Technical details:\n{ex}", LogLevel.Warn);
            }
            return texture;
        }

        /// <summary>Save a sheet's built-in art as a PNG file.</summary>
        /// <param name="sheet">The sheet to save.</param>
        /// <param name="fullPath">The absolute file path to write.</param>
        internal static void SaveBuiltInPng(SpriteSheet sheet, string fullPath)
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            byte[] rgba = BuildRgba(sheet, premultiply: false);
            using FileStream stream = File.Create(fullPath);
            WritePng(stream, sheet.Width, sheet.Height, rgba);
        }

        /// <summary>Get a sheet's built-in art as RGBA bytes (4 bytes per pixel, rows top to bottom).</summary>
        /// <param name="sheet">The sheet to build.</param>
        /// <param name="premultiply">Whether to premultiply the colours by their alpha, as MonoGame expects for textures drawn with alpha blending.</param>
        internal static byte[] BuildRgba(SpriteSheet sheet, bool premultiply)
        {
            int width = sheet.Width;
            int size = sheet.CellSize;
            byte[] rgba = new byte[width * sheet.Height * 4];

            for (int cell = 0; cell < sheet.CellCount; cell++)
            {
                string[] rows = sheet.Cells[cell];
                for (int y = 0; y < size; y++)
                {
                    string row = y < rows.Length ? rows[y] : string.Empty;
                    for (int x = 0; x < size; x++)
                    {
                        char key = x < row.Length ? row[x] : '.';
                        uint color = GetPaletteColor(key);

                        byte r = (byte)(color >> 24);
                        byte g = (byte)(color >> 16);
                        byte b = (byte)(color >> 8);
                        byte a = (byte)color;
                        if (premultiply)
                        {
                            r = (byte)((r * a + 127) / 255);
                            g = (byte)((g * a + 127) / 255);
                            b = (byte)((b * a + 127) / 255);
                        }

                        int index = ((y * width) + (cell * size) + x) * 4;
                        rgba[index] = r;
                        rgba[index + 1] = g;
                        rgba[index + 2] = b;
                        rgba[index + 3] = a;
                    }
                }
            }

            return rgba;
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Build a texture from a sheet's built-in art.</summary>
        /// <param name="sheet">The sheet to build.</param>
        /// <param name="monitor">The SMAPI monitor used for logging.</param>
        private static Texture2D? CreateBuiltInTexture(SpriteSheet sheet, IMonitor monitor)
        {
            try
            {
                byte[] rgba = BuildRgba(sheet, premultiply: true);
                Color[] pixels = new Color[sheet.Width * sheet.Height];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);

                Texture2D texture = new(Game1.graphics.GraphicsDevice, sheet.Width, sheet.Height);
                texture.SetData(pixels);
                return texture;
            }
            catch (Exception ex)
            {
                monitor.Log($"Couldn't create the {sheet.Label} sprites, so they're disabled (weather changes still work). Technical details:\n{ex}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>Get the colour for a palette character, or transparent if it's not in the palette.</summary>
        /// <param name="key">The palette character.</param>
        private static uint GetPaletteColor(char key)
        {
            foreach ((char paletteKey, uint rgba) in Palette)
            {
                if (paletteKey == key)
                    return rgba;
            }

            return 0x00000000;
        }

        /// <summary>Write an 8-bit RGBA image as a PNG file.</summary>
        /// <param name="output">The stream to write to.</param>
        /// <param name="width">The image width.</param>
        /// <param name="height">The image height.</param>
        /// <param name="rgba">The pixel data (4 bytes per pixel, rows top to bottom, not premultiplied).</param>
        private static void WritePng(Stream output, int width, int height, byte[] rgba)
        {
            // signature
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            output.Write(signature, 0, signature.Length);

            // header: size, 8 bits per channel, colour type 6 (RGBA), default compression/filter, no interlacing
            byte[] header = new byte[13];
            WriteUInt32BigEndian(header, 0, (uint)width);
            WriteUInt32BigEndian(header, 4, (uint)height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(output, "IHDR", header);

            // image data: each row starts with filter type 0 (none), then zlib-compressed
            byte[] compressed;
            using (MemoryStream buffer = new())
            {
                using (ZLibStream zlib = new(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    int stride = width * 4;
                    for (int y = 0; y < height; y++)
                    {
                        zlib.WriteByte(0);
                        zlib.Write(rgba, y * stride, stride);
                    }
                }
                compressed = buffer.ToArray();
            }
            WriteChunk(output, "IDAT", compressed);

            // end
            WriteChunk(output, "IEND", Array.Empty<byte>());
        }

        /// <summary>Write one PNG chunk (length, type, data and CRC).</summary>
        /// <param name="output">The stream to write to.</param>
        /// <param name="type">The four-letter chunk type.</param>
        /// <param name="data">The chunk data.</param>
        private static void WriteChunk(Stream output, string type, byte[] data)
        {
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            byte[] number = new byte[4];

            WriteUInt32BigEndian(number, 0, (uint)data.Length);
            output.Write(number, 0, 4);
            output.Write(typeBytes, 0, typeBytes.Length);
            output.Write(data, 0, data.Length);

            uint crc = UpdateCrc(0xFFFFFFFFu, typeBytes);
            crc = UpdateCrc(crc, data) ^ 0xFFFFFFFFu;
            WriteUInt32BigEndian(number, 0, crc);
            output.Write(number, 0, 4);
        }

        /// <summary>Update a running CRC-32 (the checksum used by PNG chunks).</summary>
        /// <param name="crc">The running CRC value.</param>
        /// <param name="data">The bytes to add.</param>
        private static uint UpdateCrc(uint crc, byte[] data)
        {
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            return crc;
        }

        /// <summary>Write a 32-bit unsigned number in big-endian byte order.</summary>
        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
