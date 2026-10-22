using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// A <see cref="BinaryWriter"/> that writes straight into its own growable byte[] (Offline-Account-Cache plan phase 2,
    /// used by the off-thread periodic account save). The overloads the save code calls write into the array without a
    /// per-call Stream round trip; every other base method writes through <see cref="BinaryWriter.OutStream"/>, an append
    /// view of the same array, so the bytes are identical to <c>new BinaryWriter(memoryStream, encoding)</c> for every
    /// overload. The array is kept across <see cref="Reset"/> calls and never shrinks (no LOH churn once it has reached
    /// the file size). Not thread-safe: one owner at a time (Envir: the game thread while !Saving).
    /// <para><see cref="BinaryWriter.Seek"/> is not supported (append only); <see cref="BinaryWriter.Flush"/> does nothing.
    /// Never dispose it while reusing it.</para>
    /// </summary>
    internal sealed class BufferBinaryWriter : BinaryWriter
    {
        private readonly Encoding _encoding;
        private byte[] _data;
        private int _count;

        public BufferBinaryWriter(Encoding encoding, int initialCapacity = 64 * 1024)
            : this(new AppendStream(), encoding, initialCapacity)
        {
        }

        private BufferBinaryWriter(AppendStream stream, Encoding encoding, int initialCapacity) : base(stream, encoding, leaveOpen: true)
        {
            stream.Owner = this;
            _encoding = encoding;
            _data = new byte[Math.Max(initialCapacity, 256)];
        }

        /// <summary>The backing array; valid bytes are [0, <see cref="Length"/>). Replaced when the writer grows.</summary>
        public byte[] Buffer => _data;

        /// <summary>Bytes written since the last <see cref="Reset"/>.</summary>
        public int Length => _count;

        /// <summary>Starts a new save: the length goes to 0, the array (and its capacity) is kept.</summary>
        public void Reset() => _count = 0;

        /// <summary>
        /// Returns the next <paramref name="size"/> bytes to fill and counts them as written. For callers that build a
        /// block in place (CharacterInfo.WriteFlags) instead of copying it in.
        /// </summary>
        public Span<byte> Reserve(int size)
        {
            if (_data.Length - _count < size) Grow(size);
            var span = new Span<byte>(_data, _count, size);
            _count += size;
            return span;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Put<T>(T value) where T : unmanaged
        {
            var count = _count;
            if (_data.Length - count < Unsafe.SizeOf<T>()) Grow(Unsafe.SizeOf<T>());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_data), count), value);
            _count = count + Unsafe.SizeOf<T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PutByte(byte value)
        {
            var count = _count;
            var data = _data;
            if ((uint)count >= (uint)data.Length) { Grow(1); data = _data; }
            data[count] = value;
            _count = count + 1;
        }

        // Little-endian, like BinaryWriter (BinaryPrimitives.Write*LittleEndian): a plain store on little-endian hardware.
        public override void Write(bool value) => PutByte((byte)(value ? 1 : 0));
        public override void Write(byte value) => PutByte(value);
        public override void Write(sbyte value) => PutByte((byte)value);
        public override void Write(short value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value); }
        public override void Write(ushort value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value); }
        public override void Write(int value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value); }
        public override void Write(uint value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value); }
        public override void Write(long value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value); }
        public override void Write(ulong value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value); }
        public override void Write(float value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value); }
        public override void Write(double value) { if (BitConverter.IsLittleEndian) Put(value); else BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value); }

        public override void Write(byte[] buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Append(buffer);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Append(buffer);

        private void Append(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        /// <summary>
        /// Same bytes and exceptions as BinaryWriter.Write(string): the encoded byte count as a 7-bit encoded int, then the
        /// bytes. With a throwing encoder fallback an invalid surrogate throws <see cref="EncoderFallbackException"/>
        /// before anything is counted as written (BinaryWriter also encodes fully before it writes).
        /// </summary>
        public override void Write(string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Length <= 127 / 3) // at most 127 bytes: a 1-byte prefix, encode in place (BinaryWriter's fast path too)
            {
                if (_data.Length - _count < 128) Grow(128);
                var written = _encoding.GetBytes(value, new Span<byte>(_data, _count + 1, 127)); // throws before _count moves
                _data[_count] = (byte)written;
                _count += written + 1;
                return;
            }

            var byteCount = _encoding.GetByteCount(value);
            var v = (uint)byteCount;
            while (v > 0x7Fu)
            {
                PutByte((byte)(v | ~0x7Fu));
                v >>= 7;
            }
            PutByte((byte)v);
            _encoding.GetBytes(value, Reserve(byteCount)); // validated by GetByteCount, so it cannot throw half-written
        }

        public override void Flush() { }

        // Doubles (at least to the needed size): a few copies on the first save, none once it has the file size.
        private void Grow(int extra)
        {
            var needed = (long)_count + extra;
            if (needed > Array.MaxLength) throw new IOException("BufferBinaryWriter would exceed the maximum array size.");
            var size = Math.Max(needed, Math.Min((long)_data.Length * 2, Array.MaxLength));
            var grown = GC.AllocateUninitializedArray<byte>((int)size);
            System.Buffer.BlockCopy(_data, 0, grown, 0, _count);
            _data = grown;
        }

        /// <summary>The append view BinaryWriter's base methods (char, decimal, Half, ...) write to.</summary>
        private sealed class AppendStream : Stream
        {
            public BufferBinaryWriter Owner;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => Owner._count;
            public override long Position { get => Owner._count; set => throw new NotSupportedException(); }

            public override void Write(byte[] buffer, int offset, int count)
            {
                ValidateBufferArguments(buffer, offset, count);
                Owner.Append(new ReadOnlySpan<byte>(buffer, offset, count));
            }

            public override void Write(ReadOnlySpan<byte> buffer) => Owner.Append(buffer);
            public override void WriteByte(byte value) => Owner.PutByte(value);
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
