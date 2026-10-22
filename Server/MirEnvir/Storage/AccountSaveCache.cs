using System.Threading;

namespace Server.MirEnvir.Storage
{
    /// <summary>[Optimisation] OfflineAccountCache (P9-12).</summary>
    public enum OfflineAccountCacheMode : byte
    {
        /// <summary>Every periodic save serialises every account (P9-11).</summary>
        Off = 0,

        /// <summary>The periodic save reuses the bytes of offline accounts that have not changed since the last save.</summary>
        On = 1,

        /// <summary>On, plus a sample of cached accounts serialised fresh and compared on every save (scratch soaks).</summary>
        Verify = 2,
    }

    /// <summary>Counters of the last periodic account save that used the offline account cache (immutable snapshot).</summary>
    public sealed class AccountCacheStats
    {
        public int Cached, Fresh, Timed, Verified, Mismatch, Epoch;
        /// <summary>The rolling compare window reached the end of the account list on this save (a lap finished).</summary>
        public bool LapComplete;
        public long MismatchTotal;
    }

    /// <summary>
    /// The thread-safe part of the offline account cache (P9-12, Offline-Account-Cache-Plan.md): the "invalidate all" epoch,
    /// the WinForms suspension and the published counters. No Envir dependency, so web admin tests can use it.
    /// <para>The cache itself (<c>AccountInfo.SaveBlob</c>) and every per-account mark are game-thread only
    /// (<c>Envir.MarkAccountDirty</c>). A blob is valid only while its tag equals <see cref="Epoch"/>; a periodic save reads
    /// the epoch once, before its loop, and tags the blobs it stores with that snapshot (review H3). Callers on other threads
    /// therefore bump <b>after</b> they mutate, so a pass that raced the edit has stored its blobs under an epoch that is
    /// already stale.</para>
    /// </summary>
    public static class AccountSaveCache
    {
        private static int _epoch;
        private static int _suspended;
        private static AccountCacheStats _last;

        /// <summary>The current epoch. A periodic save reads it once, before <see cref="Suspended"/>.</summary>
        public static int Epoch => Volatile.Read(ref _epoch);

        /// <summary>True while a WinForms account editor is open: no blob is used or stored.</summary>
        public static bool Suspended => Volatile.Read(ref _suspended) > 0;

        /// <summary>Invalidates every cached account (any thread). Call after the mutation.</summary>
        public static void InvalidateAll() => Interlocked.Increment(ref _epoch);

        /// <summary>
        /// A WinForms account editor opened (UI thread, Known debt: it edits live accounts directly). Until the matching
        /// <see cref="Resume"/> every periodic save serialises every account fresh.
        /// </summary>
        public static void Suspend()
        {
            Interlocked.Increment(ref _suspended); // before the bump: a pass that reads the new epoch then sees the suspension
            InvalidateAll();
        }

        /// <summary>The editor closed: invalidates what it may have changed, then lifts the suspension.</summary>
        public static void Resume()
        {
            InvalidateAll();
            if (Interlocked.Decrement(ref _suspended) < 0) Interlocked.Exchange(ref _suspended, 0);
        }

        /// <summary>Counters of the last save that used the cache (null until one ran). Any thread.</summary>
        public static AccountCacheStats Last => Volatile.Read(ref _last);

        internal static void Publish(AccountCacheStats stats) => Volatile.Write(ref _last, stats);
    }
}
