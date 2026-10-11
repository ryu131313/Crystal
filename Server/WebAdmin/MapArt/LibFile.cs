using System.IO.Compression;

namespace Server.WebAdmin.MapArt
{
    /// <summary>The header of one .Lib frame (no pixels).</summary>
    internal readonly struct LibFrameInfo
    {
        public readonly short Width, Height, X, Y;
        public readonly int Length;

        public LibFrameInfo(short width, short height, short x, short y, int length)
        {
            Width = width;
            Height = height;
            X = x;
            Y = y;
            Length = length;
        }

        public bool IsEmpty => Width <= 0 || Height <= 0;
    }

    /// <summary>
    /// Read-only reader of a Crystal .Lib sprite library (version 2/3, the format of Client/MirGraphics/MLibrary.cs and
    /// LibraryEditor): int version, int count, [int frameSeek], int offset[count]; each frame = short w, h, x, y, shadowX,
    /// shadowY, byte shadow, int length, then <c>length</c> bytes of gzip-compressed BGRA (w * h * 4). The mask layer is ignored.
    /// The file is opened with FileShare.ReadWrite | Delete so a running client or a patcher is never blocked. Thread-safe (one lock).
    /// </summary>
    internal sealed class LibFile : IDisposable
    {
        private readonly object _gate = new object();
        private FileStream _stream;
        private BinaryReader _reader;
        private int[] _offsets;
        private LibFrameInfo?[] _headers;
        private bool _opened, _failed;

        public LibFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public bool Exists => File.Exists(Path);

        /// <summary>Number of frames (0 when the file is missing or unreadable).</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return Open() ? _offsets.Length : 0;
                }
            }
        }

        private bool Open()
        {
            if (_opened) return !_failed;
            _opened = true;

            try
            {
                if (!File.Exists(Path))
                {
                    _failed = true;
                    return false;
                }

                _stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                _reader = new BinaryReader(_stream);

                var version = _reader.ReadInt32();
                if (version < 2) throw new InvalidDataException("Unsupported .Lib version " + version);
                var count = _reader.ReadInt32();
                if (count < 0 || count > 10_000_000) throw new InvalidDataException("Bad .Lib frame count " + count);
                if (version >= 3) _reader.ReadInt32(); // frame table seek (animation frames; not needed here)

                _offsets = new int[count];
                for (var i = 0; i < count; i++) _offsets[i] = _reader.ReadInt32();
                _headers = new LibFrameInfo?[count];
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                WebAdminLog.Error(ex, "Live monitor: cannot read " + Path);
                DisposeStreams();
                return false;
            }
        }

        public LibFrameInfo GetInfo(int index)
        {
            lock (_gate)
            {
                return InfoLocked(index);
            }
        }

        private LibFrameInfo InfoLocked(int index)
        {
            if (!Open() || index < 0 || index >= _offsets.Length) return default;

            var cached = _headers[index];
            if (cached.HasValue) return cached.Value;

            LibFrameInfo info = default;
            var offset = _offsets[index];
            if (offset > 0 && offset + 17 <= _stream.Length)
            {
                _stream.Position = offset;
                var w = _reader.ReadInt16();
                var h = _reader.ReadInt16();
                var x = _reader.ReadInt16();
                var y = _reader.ReadInt16();
                _reader.ReadInt16();
                _reader.ReadInt16();
                _reader.ReadByte();
                var length = _reader.ReadInt32();
                info = new LibFrameInfo(w, h, x, y, length);
            }

            _headers[index] = info;
            return info;
        }

        /// <summary>The frame's BGRA pixels (w * h * 4, row-major), or null when missing/empty/corrupt.</summary>
        public byte[] ReadPixels(int index, out LibFrameInfo info)
        {
            byte[] compressed;
            lock (_gate)
            {
                info = InfoLocked(index);
                if (info.IsEmpty || info.Length <= 0) return null;

                var offset = _offsets[index];
                if (offset + 17L + info.Length > _stream.Length) return null;
                _stream.Position = offset + 17;
                compressed = _reader.ReadBytes(info.Length);
            }

            var size = info.Width * info.Height * 4;
            var pixels = new byte[size];
            try
            {
                using var z = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
                var read = 0;
                while (read < size)
                {
                    var n = z.Read(pixels, read, size - read);
                    if (n <= 0) break;
                    read += n;
                }

                return read == size ? pixels : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private void DisposeStreams()
        {
            _reader?.Dispose();
            _stream?.Dispose();
            _reader = null;
            _stream = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                DisposeStreams();
                _opened = false;
                _failed = false;
                _offsets = null;
                _headers = null;
            }
        }
    }
}
