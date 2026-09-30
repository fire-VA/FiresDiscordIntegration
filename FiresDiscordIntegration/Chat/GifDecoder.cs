using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresDiscordIntegration.ChatRelay
{
    /// <summary>
    /// Lightweight GIF89a decoder. Parses raw GIF bytes into Texture2D frames
    /// and per-frame delay values. Pure C# - no external dependencies.
    ///
    /// Limitations (by design for chat embeds):
    ///   - Max 500 frames
    ///   - Max 50 MB input (media is downloaded client-side via HTTP, not Steam networking)
    ///   - Max 1024x1024 source (display size is controlled by ChatMediaEmbed)
    /// </summary>
    public static class GifDecoder
    {
        public const int MaxFrames = 500;
        public const int MaxBytes = 50 * 1024 * 1024;
        public const int MaxDimension = 1024;

        public struct GifResult
        {
            public Texture2D[] Frames;
            public float[] Delays; // seconds per frame
            public int Width;
            public int Height;
        }

        /// <summary>
        /// Decode a GIF from raw bytes.
        /// Returns null if the data is invalid or exceeds limits.
        /// </summary>
        public static GifResult? Decode(byte[] data)
        {
            if (data == null || data.Length < 13 || data.Length > MaxBytes)
                return null;

            try
            {
                using (var stream = new MemoryStream(data))
                using (var reader = new BinaryReader(stream))
                {
                    // Header
                    string sig = new string(reader.ReadChars(3));
                    string ver = new string(reader.ReadChars(3));
                    if (sig != "GIF" || (ver != "89a" && ver != "87a"))
                        return null;

                    // Logical Screen Descriptor
                    int width = reader.ReadUInt16();
                    int height = reader.ReadUInt16();
                    byte packed = reader.ReadByte();
                    reader.ReadByte(); // bg color index
                    reader.ReadByte(); // pixel aspect ratio

                    bool hasGct = (packed & 0x80) != 0;
                    int gctSize = 1 << ((packed & 0x07) + 1);

                    // Global Color Table
                    Color32[] gct = null;
                    if (hasGct)
                        gct = ReadColorTable(reader, gctSize);

                    var frames = new List<Texture2D>();
                    var delays = new List<float>();
                    Color32[] canvas = new Color32[width * height];
                    Color32[] prevCanvas = new Color32[width * height];
                    int disposalMethod = 0;
                    float frameDelay = 0.1f;
                    int transparentIndex = -1;
                    bool hasTransparency = false;

                    while (stream.Position < stream.Length && frames.Count < MaxFrames)
                    {
                        int block = reader.ReadByte();

                        if (block == 0x3B) // Trailer
                            break;

                        if (block == 0x21) // Extension
                        {
                            int label = reader.ReadByte();

                            if (label == 0xF9) // Graphics Control Extension
                            {
                                reader.ReadByte(); // block size (always 4)
                                byte gcPacked = reader.ReadByte();
                                disposalMethod = (gcPacked >> 2) & 0x07;
                                hasTransparency = (gcPacked & 0x01) != 0;
                                ushort delay = reader.ReadUInt16();
                                frameDelay = delay * 0.01f;
                                if (frameDelay < 0.02f) frameDelay = 0.1f;
                                transparentIndex = reader.ReadByte();
                                reader.ReadByte(); // terminator
                            }
                            else
                            {
                                SkipSubBlocks(reader);
                            }
                            continue;
                        }

                        if (block == 0x2C) // Image Descriptor
                        {
                            int left = reader.ReadUInt16();
                            int top = reader.ReadUInt16();
                            int fw = reader.ReadUInt16();
                            int fh = reader.ReadUInt16();
                            byte imgPacked = reader.ReadByte();

                            bool hasLct = (imgPacked & 0x80) != 0;
                            bool interlaced = (imgPacked & 0x40) != 0;
                            int lctSize = 1 << ((imgPacked & 0x07) + 1);

                            Color32[] colorTable = gct;
                            if (hasLct)
                                colorTable = ReadColorTable(reader, lctSize);

                            if (colorTable == null)
                            {
                                SkipImageData(reader);
                                continue;
                            }

                            // Save canvas for disposal
                            Array.Copy(canvas, prevCanvas, canvas.Length);

                            // Decode LZW compressed image data
                            byte[] pixels = DecodeLzw(reader, fw * fh);

                            // Render frame pixels onto canvas
                            RenderFrame(canvas, pixels, colorTable, left, top, fw, fh,
                                width, height, interlaced,
                                hasTransparency ? transparentIndex : -1);

                            // Create texture from canvas
                            var tex = CanvasToTexture(canvas, width, height);
                            frames.Add(tex);
                            delays.Add(frameDelay);

                            // Handle disposal
                            switch (disposalMethod)
                            {
                                case 2: // Restore to background
                                    for (int y = top; y < top + fh && y < height; y++)
                                        for (int x = left; x < left + fw && x < width; x++)
                                            canvas[y * width + x] = new Color32(0, 0, 0, 0);
                                    break;
                                case 3: // Restore to previous
                                    Array.Copy(prevCanvas, canvas, canvas.Length);
                                    break;
                                // 0, 1: leave canvas as-is
                            }

                            // Reset for next frame
                            transparentIndex = -1;
                            hasTransparency = false;
                            disposalMethod = 0;
                            frameDelay = 0.1f;
                        }
                    }

                    if (frames.Count == 0)
                        return null;

                    return new GifResult
                    {
                        Frames = frames.ToArray(),
                        Delays = delays.ToArray(),
                        Width = width,
                        Height = height
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ChatMedia] GIF decode error: {ex.Message}");
                return null;
            }
        }

        private static Color32[] ReadColorTable(BinaryReader reader, int count)
        {
            var table = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                table[i] = new Color32(r, g, b, 255);
            }
            return table;
        }

        private static void RenderFrame(Color32[] canvas, byte[] pixels, Color32[] colorTable,
            int left, int top, int fw, int fh, int canvasW, int canvasH,
            bool interlaced, int transparentIndex)
        {
            int[] interlaceRows = null;
            if (interlaced)
            {
                interlaceRows = new int[fh];
                int idx = 0;
                for (int pass = 0; pass < 4; pass++)
                {
                    int start = pass == 0 ? 0 : pass == 1 ? 4 : pass == 2 ? 2 : 1;
                    int step = pass == 0 ? 8 : pass == 1 ? 8 : pass == 2 ? 4 : 2;
                    for (int y = start; y < fh; y += step)
                        interlaceRows[idx++] = y;
                }
            }

            for (int y = 0; y < fh; y++)
            {
                int srcRow = interlaced ? interlaceRows[y] : y;
                int canvasY = top + srcRow;
                if (canvasY < 0 || canvasY >= canvasH) continue;

                for (int x = 0; x < fw; x++)
                {
                    int canvasX = left + x;
                    if (canvasX < 0 || canvasX >= canvasW) continue;

                    int pixIdx = y * fw + x;
                    if (pixIdx >= pixels.Length) continue;

                    int colorIdx = pixels[pixIdx] & 0xFF;
                    if (colorIdx == transparentIndex) continue;
                    if (colorIdx >= colorTable.Length) continue;

                    // GIF is top-down, Unity textures are bottom-up
                    int flippedY = canvasH - 1 - canvasY;
                    canvas[flippedY * canvasW + canvasX] = colorTable[colorIdx];
                }
            }
        }

        private static Texture2D CanvasToTexture(Color32[] canvas, int width, int height)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.SetPixels32(canvas);
            tex.Apply(false, false);
            return tex;
        }

        private static byte[] DecodeLzw(BinaryReader reader, int pixelCount)
        {
            int minCodeSize = reader.ReadByte();
            byte[] compressed = ReadSubBlocksAsBytes(reader);

            // LZW Decompress
            int clearCode = 1 << minCodeSize;
            int endCode = clearCode + 1;
            int codeSize = minCodeSize + 1;
            int nextCode = endCode + 1;
            int codeMask = (1 << codeSize) - 1;

            // Dictionary: each entry is (prefix index, suffix byte)
            // For codes < clearCode, the entry is just the byte itself
            int maxDictSize = 4096;
            int[] prefix = new int[maxDictSize];
            byte[] suffix = new byte[maxDictSize];
            int[] lengths = new int[maxDictSize];

            for (int i = 0; i < clearCode; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                lengths[i] = 1;
            }

            var output = new byte[Math.Max(pixelCount, 1)];
            int outPos = 0;

            int bitBuffer = 0;
            int bitsAvail = 0;
            int bytePos = 0;

            int prevCode = -1;

            while (outPos < pixelCount && bytePos < compressed.Length)
            {
                // Read next code
                while (bitsAvail < codeSize && bytePos < compressed.Length)
                {
                    bitBuffer |= compressed[bytePos++] << bitsAvail;
                    bitsAvail += 8;
                }
                int code = bitBuffer & codeMask;
                bitBuffer >>= codeSize;
                bitsAvail -= codeSize;

                if (code == endCode) break;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    codeMask = (1 << codeSize) - 1;
                    nextCode = endCode + 1;
                    prevCode = -1;
                    continue;
                }

                int outputCode;
                bool isNew = code >= nextCode;

                if (isNew && prevCode < 0) break;

                if (isNew)
                {
                    // Code not in dictionary yet: special case
                    // Output prevCode's string + first char of prevCode's string
                    outputCode = prevCode;
                }
                else
                {
                    outputCode = code;
                }

                // Get the string length for this code
                int len = (outputCode < nextCode && outputCode >= 0) ? lengths[outputCode] : 1;
                if (isNew) len++;

                // Output the string in reverse then flip
                int writeStart = outPos;
                int writeEnd = outPos + len - 1;

                if (writeEnd >= output.Length)
                {
                    writeEnd = output.Length - 1;
                    len = writeEnd - writeStart + 1;
                }

                // Trace back and write
                int traceCode = outputCode;
                for (int i = writeEnd; i >= writeStart; i--)
                {
                    if (traceCode < 0 || traceCode >= maxDictSize) break;
                    output[i] = suffix[traceCode];
                    traceCode = prefix[traceCode];
                }

                if (isNew && writeStart + len - 1 < output.Length)
                {
                    output[writeStart + len - 1] = output[writeStart];
                }

                outPos += len;

                // Add to dictionary
                if (prevCode >= 0 && nextCode < maxDictSize)
                {
                    prefix[nextCode] = prevCode;
                    suffix[nextCode] = output[writeStart];
                    lengths[nextCode] = lengths[prevCode] + 1;
                    nextCode++;

                    if (nextCode > codeMask && codeSize < 12)
                    {
                        codeSize++;
                        codeMask = (1 << codeSize) - 1;
                    }
                }

                prevCode = code;
            }

            return output;
        }

        private static byte[] ReadSubBlocksAsBytes(BinaryReader reader)
        {
            var result = new List<byte>();
            while (true)
            {
                int size = reader.ReadByte();
                if (size == 0) break;
                result.AddRange(reader.ReadBytes(size));
            }
            return result.ToArray();
        }

        private static void SkipSubBlocks(BinaryReader reader)
        {
            while (true)
            {
                int size = reader.ReadByte();
                if (size == 0) break;
                reader.BaseStream.Position += size;
            }
        }

        private static void SkipImageData(BinaryReader reader)
        {
            reader.ReadByte(); // LZW min code size
            SkipSubBlocks(reader);
        }
    }
}
