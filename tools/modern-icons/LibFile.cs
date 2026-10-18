using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ModernIcons
{
    /// <summary>One image of a v3 .Lib: straight (non-premultiplied) 32bpp ARGB pixels, B,G,R,A byte order.</summary>
    public sealed class LibImage
    {
        public short Width, Height, X, Y;
        public byte[] Pixels; // Width * Height * 4

        public static LibImage FromBitmap(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var img = new LibImage { Width = (short)bmp.Width, Height = (short)bmp.Height, Pixels = new byte[bmp.Width * bmp.Height * 4] };
                for (int y = 0; y < bmp.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, img.Pixels, y * bmp.Width * 4, bmp.Width * 4);
                return img;
            }
            finally { bmp.UnlockBits(data); }
        }

        public Bitmap ToBitmap()
        {
            var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < Height; y++)
                Marshal.Copy(Pixels, y * Width * 4, data.Scan0 + y * data.Stride, Width * 4);
            bmp.UnlockBits(data);
            return bmp;
        }
    }

    /// <summary>
    /// Minimal v3 .Lib writer, byte-for-byte the layout of LibraryEditor/Graphics/MLibraryV2.cs Save() + MImage.Save():
    /// int32 version (3), int32 count, int32 frameSeek, int32 offsets[count], then per image
    /// int16 Width, Height, X, Y, ShadowX, ShadowY; byte Shadow; int32 Length; Length bytes of GZip(A8R8G8B8),
    /// then at frameSeek an int32 frame count (0 here: no MirAction frame table).
    /// No mask is ever written (Shadow bit 7 clear), so the client's mask quirk (client.md §3.3) never applies.
    /// No "remove black" pass either: the pixels are written as rendered.
    /// </summary>
    public static class LibWriter
    {
        public const int LibVersion = 3;

        public static void Write(string fileName, IReadOnlyList<LibImage> images)
        {
            using var body = new MemoryStream();
            using var bw = new BinaryWriter(body);
            int headerSize = (4 + 4 + 4) + images.Count * 4;
            var offsets = new int[images.Count];
            for (int i = 0; i < images.Count; i++)
            {
                offsets[i] = (int)body.Length + headerSize;
                var img = images[i];
                byte[] gz = Compress(img.Pixels);
                bw.Write(img.Width);
                bw.Write(img.Height);
                bw.Write(img.X);
                bw.Write(img.Y);
                bw.Write((short)0); // ShadowX
                bw.Write((short)0); // ShadowY
                bw.Write((byte)0);  // Shadow (bit 7 = mask: never set)
                bw.Write(gz.Length);
                bw.Write(gz);
            }
            bw.Flush();
            int frameSeek = (int)body.Length + headerSize;

            using var fs = File.Create(fileName);
            using var w = new BinaryWriter(fs);
            w.Write(LibVersion);
            w.Write(images.Count);
            w.Write(frameSeek);
            foreach (int o in offsets) w.Write(o);
            w.Write(body.ToArray());
            w.Write(0); // frame count
        }

        private static byte[] Compress(byte[] raw)
        {
            using var memory = new MemoryStream();
            using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
                gzip.Write(raw, 0, raw.Length);
            return memory.ToArray();
        }
    }

    /// <summary>
    /// Independent reader mirroring Client/MirGraphics/MLibrary.cs: Initialize() (header, index list, v3 frame table)
    /// and MImage(reader) + CreateTexture (pixels gunzipped from offset + 17).
    /// </summary>
    public static class LibReader
    {
        public sealed class Entry { public short Width, Height, X, Y, ShadowX, ShadowY; public byte Shadow; public int Length; }

        public static (int version, Entry[] entries, int frameCount, Func<int, LibImage> decode) Open(byte[] file)
        {
            var r = new BinaryReader(new MemoryStream(file));
            int version = r.ReadInt32();
            if (version < 2) throw new InvalidDataException($"version {version} < 2 (the client would exit)");
            int count = r.ReadInt32();
            int frameSeek = version >= 3 ? r.ReadInt32() : 0;
            var index = new int[count];
            for (int i = 0; i < count; i++) index[i] = r.ReadInt32();
            int frameCount = 0;
            if (version >= 3)
            {
                r.BaseStream.Seek(frameSeek, SeekOrigin.Begin);
                frameCount = r.ReadInt32();
            }

            var entries = new Entry[count];
            for (int i = 0; i < count; i++)
            {
                r.BaseStream.Seek(index[i], SeekOrigin.Begin);
                var e = new Entry
                {
                    Width = r.ReadInt16(), Height = r.ReadInt16(), X = r.ReadInt16(), Y = r.ReadInt16(),
                    ShadowX = r.ReadInt16(), ShadowY = r.ReadInt16(), Shadow = r.ReadByte(), Length = r.ReadInt32(),
                };
                if ((e.Shadow >> 7) == 1) throw new InvalidDataException($"image {i} has a mask (generator never writes one)");
                entries[i] = e;
            }

            LibImage Decode(int i)
            {
                var e = entries[i];
                r.BaseStream.Seek(index[i] + 17, SeekOrigin.Begin);
                byte[] gz = r.ReadBytes(e.Length);
                using var gs = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                gs.CopyTo(ms);
                byte[] px = ms.ToArray();
                if (px.Length != e.Width * e.Height * 4)
                    throw new InvalidDataException($"image {i}: {px.Length} bytes, expected {e.Width * e.Height * 4}");
                return new LibImage { Width = e.Width, Height = e.Height, X = e.X, Y = e.Y, Pixels = px };
            }

            return (version, entries, frameCount, Decode);
        }
    }
}
