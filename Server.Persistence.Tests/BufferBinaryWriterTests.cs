using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Xunit.Abstractions;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Offline-Account-Cache plan phase 2: <see cref="BufferBinaryWriter"/> (the off-thread account save's writer) produces the
/// same bytes and the same exceptions as BinaryWriter over a MemoryStream with the account save encoding, for every overload.
/// </summary>
[Collection(PersistenceCollection.Name)] // the bench uses Envir.Main and the cwd
public class BufferBinaryWriterTests
{
    private readonly ITestOutputHelper _out;
    public BufferBinaryWriterTests(ITestOutputHelper output) => _out = output;

    internal static readonly Encoding SaveEncoding = (Encoding)typeof(Envir)
        .GetField("AccountSaveEncoding", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static byte[] Written(BufferBinaryWriter w) => w.Buffer.AsSpan(0, w.Length).ToArray();

    /// <summary>Runs the same writes on both writers and compares the bytes.</summary>
    private static void AssertSame(Action<BinaryWriter> write, BufferBinaryWriter w = null)
    {
        var ms = new MemoryStream();
        using (var expected = new BinaryWriter(ms, SaveEncoding, true))
            write(expected);
        w ??= new BufferBinaryWriter(SaveEncoding, 1);
        w.Reset();
        write(w);
        Assert.Equal(ms.ToArray(), Written(w));
    }

    private static string RandomString(Random rng, int length)
    {
        var sb = new StringBuilder(length);
        while (sb.Length < length)
        {
            switch (rng.Next(5))
            {
                case 0: sb.Append((char)rng.Next(0x20, 0x7F)); break;          // ASCII
                case 1: sb.Append((char)rng.Next(0x80, 0x800)); break;         // 2-byte UTF-8
                case 2: sb.Append((char)rng.Next(0x4E00, 0x9FFF)); break;      // CJK, 3 bytes
                case 3: sb.Append(char.ConvertFromUtf32(rng.Next(0x10000, 0x10FFFF))); break; // valid surrogate pair, 4 bytes
                default: sb.Append((char)rng.Next(0xE000, 0xFFFE)); break;     // private use, 3 bytes
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Every_primitive_overload_matches_BinaryWriter()
    {
        var rng = new Random(20261022);
        var w = new BufferBinaryWriter(SaveEncoding, 1);
        for (var round = 0; round < 200; round++)
        {
            var b = new byte[16]; rng.NextBytes(b);
            var bools = new byte[] { 0, 1, 2, 255 };
            // drawn once: the lambda runs on both writers
            var dec = (decimal)rng.NextDouble() * rng.Next();
            var half = (Half)rng.NextDouble();
            var ch = (char)rng.Next(0x20, 0xD7FF);
            var v7 = rng.Next(int.MinValue, int.MaxValue);
            var v7l = (long)rng.Next() << rng.Next(32);
            AssertSame(x =>
            {
                var raw = bools[round % 4];
                x.Write(System.Runtime.CompilerServices.Unsafe.As<byte, bool>(ref raw)); // non-canonical bools too
                x.Write(b[0]);
                x.Write((sbyte)b[1]);
                x.Write(BitConverter.ToInt16(b, 0));
                x.Write(BitConverter.ToUInt16(b, 2));
                x.Write(BitConverter.ToInt32(b, 4));
                x.Write(BitConverter.ToUInt32(b, 8));
                x.Write(BitConverter.ToInt64(b, 0));
                x.Write(BitConverter.ToUInt64(b, 8));
                x.Write(BitConverter.ToSingle(b, 4));   // includes NaN payloads
                x.Write(BitConverter.ToDouble(b, 8));
                x.Write(dec);
                x.Write(half);
                x.Write(ch);
                x.Write(new[] { 'a', 'é', '中' });
                x.Write(new[] { 'x', 'y', 'z', 'ü' }, 1, 3);
                x.Write("abc".AsSpan());
                x.Write(b);
                x.Write(b, 3, 7);
                x.Write(new ReadOnlySpan<byte>(b, 5, 9));
                x.Write7BitEncodedInt(v7);
                x.Write7BitEncodedInt64(v7l);
                x.Write(int.MinValue); x.Write(long.MaxValue); x.Write(double.NegativeInfinity); x.Write(-0.0f);
                x.Flush();
            }, w);
        }
    }

    [Fact]
    public void Strings_match_BinaryWriter_including_length_prefix_boundaries()
    {
        var rng = new Random(7);
        var cases = new List<string> { "", "a", "é", "中文", "\U0001F600", new string('x', 42), new string('x', 43), new string('é', 63), new string('é', 64) };
        foreach (var bytes in new[] { 127, 128, 16383, 16384, 2097151, 2097152 }) // 1/2/3/4-byte 7-bit prefixes
        {
            cases.Add(new string('a', bytes));
            cases.Add(new string('a', bytes - 2) + "é");
        }
        for (var i = 0; i < 300; i++) cases.Add(RandomString(rng, rng.Next(i < 250 ? 200 : 40000)));

        var w = new BufferBinaryWriter(SaveEncoding, 1);
        foreach (var s in cases)
            AssertSame(x => { x.Write(s); x.Write(7); }, w);
        AssertSame(x => { foreach (var s in cases) x.Write(s); }, w); // one long stream with growth
    }

    [Fact]
    public void Invalid_strings_throw_like_BinaryWriter_and_write_nothing()
    {
        foreach (var bad in new[] { "\uD800", "a\uDC00b", new string('x', 100) + "\uD83D", "\uDE00\uD83D", new string('é', 30000) + "\uDBFF" })
        {
            var ms = new MemoryStream();
            var expected = Record.Exception(() => new BinaryWriter(ms, SaveEncoding, true).Write(bad));
            var w = new BufferBinaryWriter(SaveEncoding, 1);
            w.Write(42);
            var actual = Record.Exception(() => w.Write(bad));
            Assert.NotNull(expected);
            Assert.NotNull(actual);
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(4, w.Length); // nothing of the string was written
            w.Write(43);
            Assert.Equal(new byte[] { 42, 0, 0, 0, 43, 0, 0, 0 }, Written(w));
        }

        // a lone surrogate char throws in both too
        var e1 = Record.Exception(() => new BinaryWriter(new MemoryStream(), SaveEncoding, true).Write('\uD800'));
        var e2 = Record.Exception(() => new BufferBinaryWriter(SaveEncoding).Write('\uD800'));
        Assert.Equal(e1?.GetType(), e2?.GetType());

        Assert.Throws<ArgumentNullException>(() => new BufferBinaryWriter(SaveEncoding).Write((string)null));
        Assert.Throws<ArgumentNullException>(() => new BufferBinaryWriter(SaveEncoding).Write((byte[])null));
    }

    [Fact]
    public void Reset_reuses_the_array_and_never_shrinks_it()
    {
        var w = new BufferBinaryWriter(SaveEncoding, 1);
        var payload = new byte[100_000];
        new Random(1).NextBytes(payload);
        w.Write(payload);
        var grown = w.Buffer;
        Assert.True(grown.Length >= 100_000);

        w.Reset();
        Assert.Equal(0, w.Length);
        w.Write(5);
        Assert.Same(grown, w.Buffer);
        Assert.Equal(new byte[] { 5, 0, 0, 0 }, Written(w));
        Assert.Throws<NotSupportedException>(() => w.Seek(0, SeekOrigin.Begin));
    }

    /// <summary>
    /// Bench + byte check on a COPY of real data: set MIR2_ACCOUNT_SAVE_BENCH to a folder holding Server.MirDB and
    /// Server.MirADB. Times the game-thread serialisation of the P9-10 path (reused MemoryStream + BinaryWriter) against
    /// BufferBinaryWriter, interleaved, and hashes both outputs. Passes trivially when the variable is unset.
    /// </summary>
    [Fact]
    public void Bench_real_accounts_old_vs_new()
    {
        var src = Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH");
        if (string.IsNullOrEmpty(src)) { _out.WriteLine("MIR2_ACCOUNT_SAVE_BENCH not set: bench not run."); return; }
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("MIR2_ACCOUNT_SAVE_BENCH_ROUNDS"), out var r) ? r : 20;

        var dir = NewTempFolder("acctsave-bench");
        File.Copy(Path.Combine(src, "Server.MirDB"), Path.Combine(dir, "Server.MirDB"));
        File.Copy(Path.Combine(src, "Server.MirADB"), Path.Combine(dir, "Server.MirADB"));
        InFolder(dir, () =>
        {
            Assert.True(LoadAll(), "load failed");
            var e = Envir.Main;
            var saveStream = typeof(Envir).GetMethod("SaveAccounts", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(Stream), typeof(bool) })!;
            var saveWriter = typeof(Envir).GetMethod("SaveAccounts", BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(BinaryWriter) })!;
            var oldSave = (Action<Stream, bool>)Delegate.CreateDelegate(typeof(Action<Stream, bool>), e, saveStream);
            var newSave = (Action<BinaryWriter>)Delegate.CreateDelegate(typeof(Action<BinaryWriter>), e, saveWriter);

            var ms = new MemoryStream();
            var w = new BufferBinaryWriter(SaveEncoding, (int)(new FileInfo(Envir.AccountPath).Length * 9 / 8));
            var oldMs = new List<double>(); var newMs = new List<double>();
            static double Ms(long from) => (System.Diagnostics.Stopwatch.GetTimestamp() - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            for (var i = -2; i < rounds; i++) // two warm-up rounds
            {
                double RunOld() { var t = System.Diagnostics.Stopwatch.GetTimestamp(); ms.SetLength(0); oldSave(ms, true); return Ms(t); }
                double RunNew() { var t = System.Diagnostics.Stopwatch.GetTimestamp(); w.Reset(); newSave(w); return Ms(t); }
                double o, n;
                if ((i & 1) == 0) { o = RunOld(); n = RunNew(); } // alternate the order: each run evicts the other's buffer
                else { n = RunNew(); o = RunOld(); }
                if (i < 0) continue;
                oldMs.Add(o);
                newMs.Add(n);
            }

            var oldHash = Convert.ToHexString(SHA256.HashData(ms.GetBuffer().AsSpan(0, (int)ms.Length)));
            var newHash = Convert.ToHexString(SHA256.HashData(w.Buffer.AsSpan(0, w.Length)));
            oldMs.Sort(); newMs.Sort();
            _out.WriteLine($"accounts={e.AccountList.Count} characters={e.CharacterList.Count} heroes={e.HeroList.Count} bytes old={ms.Length} new={w.Length}");
            _out.WriteLine($"old (MemoryStream+BinaryWriter): median={oldMs[oldMs.Count / 2]:0.00} ms min={oldMs[0]:0.00} max={oldMs[^1]:0.00}");
            _out.WriteLine($"new (BufferBinaryWriter):        median={newMs[newMs.Count / 2]:0.00} ms min={newMs[0]:0.00} max={newMs[^1]:0.00}");
            _out.WriteLine($"sha256 old={oldHash}");
            _out.WriteLine($"sha256 new={newHash}");
            Assert.Equal(oldHash, newHash);
            return 0;
        });
        Directory.Delete(dir, recursive: true);
    }
}
