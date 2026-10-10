using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Shared.Tests;

/// <summary>Reflection helpers over the Shared packet classes. Test-only.</summary>
internal static class PacketTestSupport
{
    public static readonly Assembly SharedAssembly = typeof(Packet).Assembly;

    private static readonly MethodInfo ReadMethod =
        typeof(Packet).GetMethod("ReadPacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo WriteMethod =
        typeof(Packet).GetMethod("WritePacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetClientPacketMethod =
        typeof(Packet).GetMethod("GetClientPacket", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
    private static readonly MethodInfo GetServerPacketMethod =
        typeof(Packet).GetMethod("GetServerPacket", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    public static IEnumerable<Type> PacketTypes(string ns) =>
        SharedAssembly.GetTypes()
            .Where(t => t.Namespace == ns && !t.IsAbstract && typeof(Packet).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    public static IEnumerable<Type> ServerPacketTypes => PacketTypes("ServerPackets");
    public static IEnumerable<Type> ClientPacketTypes => PacketTypes("ClientPackets");

    public static Packet CreateInstance(Type t) => (Packet)Activator.CreateInstance(t, nonPublic: true)!;

    public static Packet GetClientPacket(short id) => (Packet)GetClientPacketMethod.Invoke(null, new object[] { id });
    public static Packet GetServerPacket(short id) => (Packet)GetServerPacketMethod.Invoke(null, new object[] { id });

    public static byte[] Write(Packet p)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            Invoke(WriteMethod, p, w);
        }
        return ms.ToArray();
    }

    /// <summary>Reads <paramref name="payload"/> into <paramref name="p"/>; returns bytes consumed.</summary>
    public static long Read(Packet p, byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms);
        Invoke(ReadMethod, p, r);
        return ms.Position;
    }

    private static void Invoke(MethodInfo m, object target, object arg)
    {
        try { m.Invoke(target, new[] { arg }); }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
    }

    /// <summary>
    /// Best-effort fixture: fills null strings/objects/collections so WritePacket can run.
    /// Value types keep their defaults except bools/numbers set to a non-zero marker where safe.
    /// </summary>
    public static void Fill(object o, int depth = 6)
    {
        if (o == null || depth <= 0) return;
        foreach (var f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (f.IsInitOnly && f.FieldType.IsValueType) continue;
            var v = f.GetValue(o);
            if (v != null)
            {
                if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && !(v is IEnumerable)) Fill(v, depth - 1);
                continue;
            }
            var made = Make(f.FieldType, depth - 1);
            if (made != null && !f.IsInitOnly) f.SetValue(o, made);
        }
    }

    private static object Make(Type t, int depth)
    {
        if (t == typeof(string)) return "a";
        if (t == typeof(byte[])) return Array.Empty<byte>();
        if (t.IsArray) return Array.CreateInstance(t.GetElementType()!, 0);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return Activator.CreateInstance(t);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)) return Activator.CreateInstance(t);
        if (t.IsInterface || t.IsAbstract || t == typeof(object) || typeof(Delegate).IsAssignableFrom(t)) return null;
        if (depth <= 0) return null;
        object inst;
        try
        {
            inst = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) != null
                ? Activator.CreateInstance(t, nonPublic: true)
                // No parameterless ctor (e.g. UserItem(ItemInfo)): build without running a ctor,
                // then fill nulls. Field initializers do not run, so Fill supplies collections.
                : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
        }
        catch
        {
            return null; // the fixture will report it
        }
        Fill(inst, depth);
        return inst;
    }

    /// <summary>Declared instance fields in metadata (declaration) order, as "name:type".</summary>
    public static string FieldSignature(Type t)
    {
        var parts = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.MetadataToken)
            .Select(f => f.Name + ":" + f.FieldType.Name);
        return string.Join(";", parts);
    }

    public static string ShortHash(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 4);
    }

    public static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Legend of Mir.sln"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Repo root (Legend of Mir.sln) not found above " + AppContext.BaseDirectory);
        return Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
    }
}
