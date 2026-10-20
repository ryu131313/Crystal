namespace Server.MirEnvir.Storage
{
    /// <summary>
    /// Where system (design) data lives: the Server.MirDB lists, index counters and the respawn tick (roadmap P7 phase 7.1).
    /// Accounts, guilds, goods and conquests are not part of it; they stay in Envir and stay binary.
    /// <para>The store only reads and writes. <see cref="Envir.LoadDB"/> holds <see cref="Envir.LoadLock"/> around
    /// <see cref="LoadDesign"/> and then runs <see cref="WorldBinder.Bind"/>, so every backend gets the same post-load bindings.</para>
    /// </summary>
    internal interface IWorldStore
    {
        /// <summary>Fills <paramref name="target"/>'s design lists and counters and sets Envir.LoadVersion/LoadCustomVersion.
        /// False (after logging why) when the data can't be loaded. Caller holds LoadLock.</summary>
        bool LoadDesign(Envir target);

        /// <summary>Writes <paramref name="source"/>'s design data. <paramref name="backupFileName"/> names the backup of the
        /// previous data (null = the backend's default name). Web callers saving Envir.Edit hold EditLock (GameDataSave);
        /// no lock is taken here.</summary>
        void SaveDesign(Envir source, string backupFileName);
    }
}
