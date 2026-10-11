using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Server.WebAdmin.GameData;

namespace Server.WebAdmin.LiveDb
{
    /// <summary>The inverse of one journaled batch, or why it cannot be undone.</summary>
    internal sealed class LiveDbUndoPlan
    {
        public string TargetId;

        /// <summary>The inverse batch (null when nothing can be built). Runs through the normal <see cref="LiveDbEngine"/> pipeline.</summary>
        public LiveDbBatchRequest Request;

        /// <summary>Rows that make the whole undo impossible: {endpoint, index, kind, message}.</summary>
        public readonly List<Dictionary<string, object>> Refused = new List<Dictionary<string, object>>();

        /// <summary>Sentences about rows the running world keeps until a restart, other saved changes the undo does not revert, ...</summary>
        public readonly List<string> Notes = new List<string>();

        public int Updates;
        public int Deletes;

        public bool Undoable => Refused.Count == 0 && Request != null && Request.OpCount > 0;
    }

    /// <summary>The outcome of POST /api/admin/livedb/undo/{batchId}.</summary>
    internal sealed class LiveDbUndoOutcome
    {
        public string TargetId;
        public string UndoId;

        /// <summary>The target is already undone by <see cref="UndoId"/>: <see cref="Result"/> is that batch's stored outcome.</summary>
        public bool AlreadyUndone;

        public LiveDbUndoPlan Plan;
        public LiveDbBatchResult Result;
    }

    /// <summary>
    /// LiveDb undo. <see cref="Plan"/> builds the inverse batch of a journaled batch (pure; no Edit / Main access):
    /// <list type="bullet">
    /// <item>updates → the recorded before-values, guarded by the row's <c>revAfter</c> (any later change of the row is a conflict);</item>
    /// <item>inserts → deletes guarded by <c>revAfter</c> (<see cref="LiveDbTableOps.DeleteRevs"/>). A position-keyed key
    /// (<c>parent * 10000 + position</c>) only names the same row while no earlier sibling was deleted: the rev guard refuses it
    /// otherwise. Tables whose deletes are restart-only (items, monsters, NPCs) lose the row from the saved data only: the running world
    /// keeps it until a restart (said in the notes);</item>
    /// <item>deletes → refused: a re-insert would get a new RespawnIndex / GIndex or go to the end of a position-keyed list. The way back
    /// is a backup restore (Backups &amp; History, world stopped).</item>
    /// </list>
    /// The inverse runs through the same pipeline (validate-all, save, live step, journal, audit), so it is all-or-nothing and reports
    /// conflicts per row. Its id is derived from the target (<see cref="UndoId"/>), so a double click returns the first outcome; undoing
    /// an undo (redo) is an ordinary undo of that batch, and undoing the original again after a redo uses the next generation id.
    /// </summary>
    internal static class LiveDbUndo
    {
        public const int MaxGenerations = 50;
        private const int MaxDepth = 16;

        public const string DeleteRefusal = "deleted rows cannot be undone (a re-insert would get a new index or go to the end of its list); restore a backup of " +
                                            "Server.MirDB with the world stopped (Backups & History) to get the row back";

        /// <summary>
        /// The deterministic id of the <paramref name="generation"/>-th undo of a batch: <c>undo:&lt;id&gt;</c>, then <c>undo2:&lt;id&gt;</c>, ...
        /// An id that would pass <see cref="LiveDbBatchRequest.MaxIdLength"/> uses a hash of the target instead.
        /// </summary>
        public static string UndoId(string targetId, int generation)
        {
            var prefix = generation <= 1 ? "undo:" : "undo" + generation.ToString(CultureInfo.InvariantCulture) + ":";
            targetId ??= string.Empty;
            if (prefix.Length + targetId.Length <= LiveDbBatchRequest.MaxIdLength) return prefix + targetId;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(targetId));
            return prefix + "h" + Convert.ToHexString(hash, 0, 20).ToLowerInvariant();
        }

        /// <summary>
        /// The id the next undo of <paramref name="targetId"/> uses, and — when the target is currently undone — the stored outcome of
        /// the undo that did it (a repeated click). An undo that was itself undone (redo) moves on to the next generation.
        /// </summary>
        public static (string Id, LiveDbBatchResult Prior) ResolveId(LiveDbJournal journal, string targetId)
        {
            for (var gen = 1; gen <= MaxGenerations; gen++)
            {
                var id = UndoId(targetId, gen);
                var prior = journal.Find(id);
                if (prior == null) return (id, null);
                if (!IsUndone(journal, id, 1)) return (id, prior);
            }

            throw new WebAdminException(409, "This batch was undone and redone too many times; edit the rows directly");
        }

        /// <summary>True when an applied undo of <paramref name="id"/> exists that was not itself undone.</summary>
        public static bool IsUndone(LiveDbJournal journal, string id) => IsUndone(journal, id, 0);

        private static bool IsUndone(LiveDbJournal journal, string id, int depth)
        {
            if (depth > MaxDepth) return false;
            for (var gen = 1; gen <= MaxGenerations; gen++)
            {
                var undo = journal.Find(UndoId(id, gen));
                if (undo == null) return false;
                if (!IsUndone(journal, undo.Id, depth + 1)) return true;
            }

            return false;
        }

        /// <summary>Builds the inverse batch of <paramref name="target"/> (pure).</summary>
        /// <param name="deleteLive">Whether deletes of a table reach the running world (default: <see cref="LiveDbClassification.DeleteLive"/>).</param>
        public static LiveDbUndoPlan Plan(LiveDbBatchResult target, string undoId, Func<string, bool> deleteLive = null)
        {
            deleteLive ??= LiveDbClassification.DeleteLive;
            var plan = new LiveDbUndoPlan { TargetId = target?.Id };
            if (target == null) return plan;

            if (!string.Equals(target.Status, "applied", StringComparison.Ordinal))
            {
                plan.Refused.Add(Refusal(null, 0, null, "only applied batches can be undone (this one is " + (target.Status ?? "unknown") + ")"));
                return plan;
            }

            var req = new LiveDbBatchRequest
            {
                Id = undoId,
                InverseOf = target.Id,
                Source = "undo",
            };

            var byEndpoint = new Dictionary<string, LiveDbTableOps>(StringComparer.OrdinalIgnoreCase);
            var keptLive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in target.Rows ?? new List<LiveDbRowOutcome>())
            {
                if (row == null || string.IsNullOrEmpty(row.Endpoint)) continue;
                var index = row.NewIndex != 0 ? row.NewIndex : row.Index;

                switch (row.Kind)
                {
                    case "delete":
                        plan.Refused.Add(Refusal(row.Endpoint, index, row.Kind, DeleteRefusal));
                        continue;

                    case "insert":
                    {
                        if (index <= 0 || string.IsNullOrEmpty(row.RevAfter))
                        {
                            plan.Refused.Add(Refusal(row.Endpoint, index, row.Kind, "the journal has no index / revision for the new row"));
                            continue;
                        }

                        var ops = Ops(byEndpoint, req, row.Endpoint);
                        if (ops.Deletes.Contains(index))
                        {
                            plan.Refused.Add(Refusal(row.Endpoint, index, row.Kind, "the batch names this row twice"));
                            continue;
                        }

                        ops.Deletes.Add(index);
                        ops.DeleteRevs[index] = row.RevAfter;
                        plan.Deletes++;
                        if (!deleteLive(row.Endpoint) && row.RowLive == true)
                        {
                            keptLive.TryGetValue(row.Endpoint, out var n);
                            keptLive[row.Endpoint] = n + 1;
                        }

                        continue;
                    }

                    default: // update
                    {
                        var values = BeforeValues(row);
                        if (values.Count == 0) continue; // nothing recorded to put back
                        if (index <= 0 || string.IsNullOrEmpty(row.RevAfter))
                        {
                            plan.Refused.Add(Refusal(row.Endpoint, index, row.Kind, "the journal has no revision for this row after the batch"));
                            continue;
                        }

                        var ops = Ops(byEndpoint, req, row.Endpoint);
                        var wire = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["Index"] = index,
                            [GameDataCodec.RevKey] = row.RevAfter,
                        };
                        foreach (var pair in values) wire[pair.Key] = pair.Value;

                        JsonElement element;
                        try
                        {
                            element = JsonSerializer.SerializeToElement(wire, WebAdminJson.Options);
                        }
                        catch (Exception ex) when (ex is NotSupportedException || ex is JsonException || ex is InvalidOperationException)
                        {
                            plan.Refused.Add(Refusal(row.Endpoint, index, row.Kind, "the recorded values cannot be replayed"));
                            continue;
                        }

                        ops.Rows.Add(element);
                        plan.Updates++;
                        continue;
                    }
                }
            }

            foreach (var pair in keptLive)
            {
                var label = GameDataRegistry.Get(pair.Key)?.Label ?? pair.Key;
                plan.Notes.Add(label + ": " + pair.Value.ToString(CultureInfo.InvariantCulture) + " new row(s) removed from the saved data only; the running world keeps " +
                               (pair.Value == 1 ? "it" : "them") + " until a restart (deletes of this table take effect after a restart).");
            }

            if (target.FlushedAreas != null && target.FlushedAreas.Count > 0)
                plan.Notes.Add("The original batch also saved other editor changes (" + string.Join(", ", target.FlushedAreas) + "); undo does not revert those.");

            if (plan.Refused.Count == 0 && req.OpCount > 0)
            {
                req.Notes.AddRange(plan.Notes);
                plan.Request = req;
            }

            return plan;
        }

        /// <summary>
        /// Undoes a journaled batch through the LiveDb pipeline (call it from a worker thread, never the game thread). Throws
        /// <see cref="WebAdminException"/> 404 (not in the journal), 409 (not undoable: deletes, missing revisions; or nothing to undo).
        /// </summary>
        public static LiveDbUndoOutcome Run(LiveDbEnvironment env, string targetId, bool dryRun, string admin, string ip, string reason, bool flushUnsaved)
        {
            if (env == null) throw new ArgumentNullException(nameof(env));
            targetId = (targetId ?? string.Empty).Trim();
            if (!LiveDbBatchRequest.IsValidId(targetId)) throw new WebAdminException(400, "Invalid batch id");

            var target = env.Journal.Find(targetId);
            if (target == null)
                throw new WebAdminException(404, "Batch '" + targetId + "' is not in the LiveDb journal (the last " +
                                                 LiveDbJournal.LookupDays.ToString(CultureInfo.InvariantCulture) + " days)");

            var (undoId, prior) = ResolveId(env.Journal, targetId);
            var outcome = new LiveDbUndoOutcome { TargetId = targetId, UndoId = undoId };
            if (prior != null)
            {
                var copy = prior.ShallowCopy();
                copy.Duplicate = true;
                copy.HttpStatus = 200;
                outcome.AlreadyUndone = true;
                outcome.Result = copy;
                return outcome;
            }

            var plan = Plan(target, undoId, env.DeleteLive);
            outcome.Plan = plan;
            if (plan.Refused.Count > 0)
                throw new WebAdminException(409, "Batch " + targetId + " cannot be undone: " + plan.Refused[0]["message"] +
                                                 (plan.Refused.Count > 1 ? " (+" + (plan.Refused.Count - 1).ToString(CultureInfo.InvariantCulture) + " more)" : string.Empty) +
                                                 ". Nothing was changed.",
                    new { refused = plan.Refused, backupFile = target.BackupFile, target = targetId });
            if (!plan.Undoable) throw new WebAdminException(409, "Batch " + targetId + " has nothing to undo", new { target = targetId });

            var req = plan.Request;
            req.DryRun = dryRun;
            req.Admin = admin;
            req.Ip = ip;
            req.FlushUnsaved = flushUnsaved;
            req.Reason = "Undo of batch " + targetId + (string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason.Trim());
            outcome.Result = new LiveDbEngine(env).Run(req);
            return outcome;
        }

        private static LiveDbTableOps Ops(Dictionary<string, LiveDbTableOps> byEndpoint, LiveDbBatchRequest req, string endpoint)
        {
            if (byEndpoint.TryGetValue(endpoint, out var ops)) return ops;
            ops = new LiveDbTableOps { Endpoint = endpoint };
            byEndpoint[endpoint] = ops;
            req.Tables.Add(ops);
            return ops;
        }

        /// <summary>The recorded before-values of an updated row (the before-image, else the changes' before values).</summary>
        private static List<KeyValuePair<string, object>> BeforeValues(LiveDbRowOutcome row)
        {
            var list = new List<KeyValuePair<string, object>>();
            if (row.BeforeImage != null && row.BeforeImage.Count > 0)
            {
                foreach (var pair in row.BeforeImage)
                    if (!string.Equals(pair.Key, "Index", StringComparison.OrdinalIgnoreCase) && !pair.Key.StartsWith("__", StringComparison.Ordinal))
                        list.Add(new KeyValuePair<string, object>(pair.Key, pair.Value));
                return list;
            }

            if (row.Changes != null)
                foreach (var c in row.Changes)
                    if (c != null && !string.IsNullOrEmpty(c.Field)) list.Add(new KeyValuePair<string, object>(c.Field, c.Before));
            return list;
        }

        private static Dictionary<string, object> Refusal(string endpoint, int index, string kind, string message)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["endpoint"] = endpoint,
                ["index"] = index,
                ["kind"] = kind,
                ["message"] = message,
            };
        }
    }
}
