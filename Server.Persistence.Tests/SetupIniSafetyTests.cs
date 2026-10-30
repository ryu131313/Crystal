using Server.MirEnvir.Storage;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>Setup.ini must never turn into a file of defaults because it read as empty (the exit save killed by a Windows restart, 2026-10-28).</summary>
[Collection(PersistenceCollection.Name)]
public class SetupIniSafetyTests
{
    private const string Good = "[Network]\r\nPort=7100\r\n";
    private const string OtherGood = "[Network]\r\nPort=7200\r\n";

    private static void InConfigs(string tag, Action<string> body)
    {
        var dir = NewTempFolder(tag);
        InFolder(dir, () =>
        {
            Directory.CreateDirectory("Configs");
            body(Path.Combine("Configs", "Setup.ini"));
            return 0;
        });
    }

    [Fact]
    public void Save_keeps_the_previous_file_as_the_backup_and_leaves_no_temp()
    {
        InConfigs("setupini-save", path =>
        {
            File.WriteAllText(path, Good);
            var reader = new SetupIniReader(path);
            reader.BeginBatch();
            reader.Write("Network", "Port", (ushort)7200);
            reader.Write("Network", "MaxUser", 10);
            Assert.Contains("Port=7100", File.ReadAllLines(path));
            reader.EndBatch();

            Assert.Contains("Port=7200", File.ReadAllLines(path));
            Assert.Contains("MaxUser=10", File.ReadAllLines(path));
            Assert.Contains("Port=7100", File.ReadAllLines(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void First_save_without_a_file_creates_it()
    {
        InConfigs("setupini-new", path =>
        {
            var reader = new SetupIniReader(path);
            Assert.Equal(7000, reader.ReadUInt16("Network", "Port", 7000));
            reader.Save();

            Assert.Contains("Port=7000", File.ReadAllLines(path));
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0\0")]
    public void Damaged_file_is_restored_from_the_backup_and_the_backup_survives_the_next_save(string damaged)
    {
        InConfigs("setupini-recover", path =>
        {
            File.WriteAllText(path, damaged);
            File.WriteAllText(path + ".bak", Good);

            var reader = new SetupIniReader(path);
            Assert.Equal(7100, reader.ReadUInt16("Network", "Port", 7000));

            reader.Save();
            Assert.Contains("Port=7100", File.ReadAllLines(path));
            Assert.Contains("Port=7100", File.ReadAllLines(path + ".bak"));
        });
    }

    [Fact]
    public void Damaged_file_without_a_backup_is_not_overwritten_with_defaults()
    {
        InConfigs("setupini-blocked", path =>
        {
            File.WriteAllText(path, string.Empty);

            var reader = new SetupIniReader(path);
            Assert.Equal(7000, reader.ReadUInt16("Network", "Port", 7000));
            reader.Save();

            Assert.Equal(0, new FileInfo(path).Length);
            Assert.False(File.Exists(path + ".tmp"));
        });
    }

    [Fact]
    public void File_locked_at_the_first_read_is_read_again_instead_of_treated_as_damaged()
    {
        InConfigs("setupini-locked", path =>
        {
            File.WriteAllText(path, Good);
            File.WriteAllText(path + ".bak", OtherGood);

            var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            var release = Task.Run(async () =>
            {
                await Task.Delay(100);
                locker.Dispose();
            });

            var reader = new SetupIniReader(path);
            release.Wait();

            Assert.Equal(7100, reader.ReadUInt16("Network", "Port", 7000));
        });
    }
}
