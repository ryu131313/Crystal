using Server.MirDatabase;
using Server.MirEnvir;
using static Server.Persistence.Tests.PersistenceHarness;

namespace Server.Persistence.Tests;

/// <summary>
/// Codebase review 2026-10-24 #1: CharacterInfo.Save wrote Friends.Count but skipped friends whose character left
/// CharacterList, so the next load parsed the following fields as friend records.
/// </summary>
[Collection(PersistenceCollection.Name)]
public class FriendSaveTests
{
    [Fact]
    public void Friend_of_a_removed_character_is_dropped_and_the_file_still_loads()
    {
        var dir = NewTempFolder("friends");
        InFolder(dir, () =>
        {
            EnsureBuffInfo();
            ResetMain();
            SyntheticWorld.Populate(Envir.Main);

            var owner = Envir.Main.CharacterList[0];
            var other = Envir.Main.CharacterList[1];
            owner.Friends.Clear();
            owner.Friends.Add(new FriendInfo(other, false) { Memo = "kept" });
            owner.Friends.Add(new FriendInfo(other, true) { Index = 987654, Memo = "gone" }); // no such character
            int ownerIndex = owner.Index;
            SaveAll();

            Assert.True(LoadAll(), "load failed");
            var loaded = Envir.Main.GetCharacterInfo(ownerIndex);
            Assert.NotNull(loaded);
            Assert.Single(loaded.Friends);
            Assert.Equal("kept", loaded.Friends[0].Memo);
            Assert.Equal(other.Index, loaded.Friends[0].Index);
            return 0;
        });
        Directory.Delete(dir, recursive: true);
    }
}
