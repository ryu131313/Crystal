using System.Globalization;
using System.Text.Json;
using Server.MirEnvir;
using Server.MirEnvir.Storage;
using Server.WebAdmin.GameData;
using Server.WebAdmin.MapArt;
using Server.WebAdmin.MapEditor;

namespace Server.WebAdmin.Quests
{
    /// <summary>
    /// The web admin Quests editor (UserData-and-Quests-Plan.md, Part B, "Authoring"; page wwwroot\admin\admin-quests.js):
    /// <list type="bullet">
    /// <item>GET  /api/admin/quests                       → {quests:[{index, name, authorKey, category, groupName, errors, warnings, …}]}</item>
    /// <item>GET  /api/admin/quests/meta                  → enum names, groups, shared conditions, storylines, pools</item>
    /// <item>GET  /api/admin/quests/{index}               → the quest graph (<see cref="QuestGraphDto"/>, with rev)</item>
    /// <item>POST /api/admin/quests/save {quest, dryRun?, reason?} → saved (422 + data.problems when validation fails)</item>
    /// <item>POST /api/admin/quests/{index}/delete {rev, reason?}</item>
    /// <item>GET  /api/admin/quests/where-used?kind=monster|item|npc|map|group|condition|quest|spell&amp;id=n</item>
    /// <item>GET  /api/admin/quests/hints?kind=monster|item|npc|map&amp;id=n → picker hints (spawns, drops, an NPC's place, a map's respawns)</item>
    /// <item>GET  /api/admin/quests/item-icon/{index}       → the client's item icon (Data\Items.Lib) as a PNG</item>
    /// <item>GET  /api/admin/quests/{index}/export        → the crystal-quest/1 file (download)</item>
    /// <item>POST /api/admin/quests/import {file, apply?, token?} → preview (diff, problems, token), then apply with the token</item>
    /// <item>POST /api/admin/quests/groups/save {group}, POST /api/admin/quests/groups/{index}/delete</item>
    /// </list>
    /// Saves write SystemData.db through Envir.Edit under EditLock (<see cref="QuestEditorGate"/>), audit as <c>quests.*</c>, and call
    /// <see cref="QuestEditNotifier.QuestsChangedAsync"/> (the quest-replace hook), whose outcome the response carries as <c>live</c>.
    /// Threads: HTTP workers only; the world is touched only inside the hook's one admin action on the game thread.
    /// </summary>
    public sealed class QuestEditorRoutes : IWebAdminRoutes
    {
        public void Register(WebAdminRouter router)
        {
            router.Get("/api/admin/quests", GetList);
            router.Get("/api/admin/quests/meta", GetMeta);
            router.Get("/api/admin/quests/where-used", GetWhereUsed);
            router.Get("/api/admin/quests/hints", GetHints);
            router.Get("/api/admin/quests/item-icon/{index:int}", GetItemIcon);
            router.Get("/api/admin/quests/{index:int}", GetQuest);
            router.Get("/api/admin/quests/{index:int}/export", GetExport);
            router.Post("/api/admin/quests/save", PostSave);
            router.Post("/api/admin/quests/{index:int}/delete", PostDelete);
            router.Post("/api/admin/quests/import", PostImport);
            router.Post("/api/admin/quests/groups/save", PostGroupSave);
            router.Post("/api/admin/quests/groups/{index:int}/delete", PostGroupDelete);
        }

        private static Task GetList(WebAdminRequest req)
        {
            var list = QuestEditorGate.Read(s => s.List());
            return req.Json(new { quests = list });
        }

        private static Task GetMeta(WebAdminRequest req) => req.Json(QuestEditorGate.Read(s => s.Meta()));

        private static Task GetQuest(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var dto = QuestEditorGate.Read(s => s.Read(index));
            if (dto == null) throw new WebAdminException(404, "Quest " + index.ToString(CultureInfo.InvariantCulture) + " does not exist");
            return req.Json(dto);
        }

        private static Task GetWhereUsed(WebAdminRequest req)
        {
            if (!QuestWhereUsed.TryParseKind(req.Query["kind"], out var kind))
                throw new WebAdminException(400, "'kind' must be one of: " + string.Join(", ", Enum.GetNames(typeof(QuestRefKind)).Select(n => n.ToLowerInvariant())));
            var id = req.QueryInt("id", 0);
            if (id == 0) throw new WebAdminException(400, "'id' is required");
            var uses = QuestEditorGate.Read(s => s.WhereUsed(kind, id));
            return req.Json(new { kind = kind.ToString().ToLowerInvariant(), id, uses });
        }

        private static Task GetHints(WebAdminRequest req)
        {
            var kind = (req.Query["kind"] ?? string.Empty).Trim().ToLowerInvariant();
            if (kind != "monster" && kind != "item" && kind != "npc" && kind != "map")
                throw new WebAdminException(400, "'kind' must be one of: monster, item, npc, map");
            var id = req.QueryInt("id", 0);
            if (id <= 0) throw new WebAdminException(400, "'id' is required");

            var hint = QuestEditorGate.Read(s => s.Hint(kind, id));
            if (hint == null) throw new WebAdminException(404, "No " + kind + " #" + id.ToString(CultureInfo.InvariantCulture));

            if (hint is QuestHints.ItemHint item)
            {
                // Drop tables are read without EditLock (as DropRoutes): take the monster snapshot under it, then walk them outside.
                item.dropsKnown = DesignStore.Active;
                if (DesignStore.Active)
                {
                    var monsters = new List<(int Index, string Name, string DropPath)>();
                    lock (WebAdminGame.EditLock)
                    {
                        var list = Envir.Edit.MonsterInfoList;
                        for (var i = 0; i < list.Count; i++) if (list[i] != null) monsters.Add((list[i].Index, list[i].Name, list[i].DropPath));
                    }

                    item.droppedBy = QuestHints.DroppedBy(id, monsters, (i, n, p) => DropTables.ForMonster(i, n, p), DropTables.Get);
                }
            }
            else if (hint is QuestHints.MapHint map)
            {
                var walk = MapEditorService.Walkability(map.fileName);   // file IO outside EditLock
                map.width = walk?.Width ?? 0;
                map.height = walk?.Height ?? 0;
            }

            return req.Json(hint);
        }

        private static Task GetItemIcon(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var image = QuestEditorGate.Read(s => s.ItemImage(index))
                        ?? throw new WebAdminException(404, "Item #" + index.ToString(CultureInfo.InvariantCulture) + " does not exist");
            var png = MapArtService.ItemIconPng(image)
                      ?? throw new WebAdminException(404, "No item icon (is [WebAdmin] ClientDataPath set?)");
            req.SetHeader("Cache-Control", "private, max-age=300");
            return req.Bytes(png, "image/png");
        }

        private static Task GetExport(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var file = QuestEditorGate.Read(s => s.Export(index, DateTime.UtcNow));
            if (file == null) throw new WebAdminException(404, "Quest " + index.ToString(CultureInfo.InvariantCulture) + " does not exist");
            var json = JsonSerializer.Serialize(file, IndentedOptions.Value);
            var name = SafeFileName(string.IsNullOrEmpty(file.Quest.AuthorKey) ? "quest-" + index.ToString(CultureInfo.InvariantCulture) : file.Quest.AuthorKey) + ".quest.json";
            return req.Bytes(WebAdminJson.Utf8NoBom.GetBytes(json), "application/json; charset=utf-8", name);
        }

        private static readonly Lazy<JsonSerializerOptions> IndentedOptions = new Lazy<JsonSerializerOptions>(() => new JsonSerializerOptions(WebAdminJson.Options) { WriteIndented = true });

        private static string SafeFileName(string s)
        {
            var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray();
            var name = new string(chars).Trim('.');
            return name.Length == 0 ? "quest" : name.Length > 80 ? name.Substring(0, 80) : name;
        }

        private static string Reason(JsonElement body)
        {
            var reason = body.ValueKind == JsonValueKind.Object ? body.GetString("reason") : null;
            return string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        }

        private static async Task PostSave(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("quest", out var questEl) || questEl.ValueKind != JsonValueKind.Object)
                throw new WebAdminException(400, "Expected a JSON object body {quest, dryRun?, reason?}");
            QuestGraphDto dto;
            try
            {
                dto = questEl.Deserialize<QuestGraphDto>(WebAdminJson.Options);
            }
            catch (JsonException)
            {
                throw new WebAdminException(400, "'quest' is not a valid quest graph");
            }

            var dryRun = body.GetBool("dryRun");
            if (dryRun)
            {
                var dry = await Task.Run(() => QuestEditorGate.Read(s => s.Save(dto, true))).ConfigureAwait(false);
                await req.Ok(dry, "Check passed: " + Count(dry.Problems) + ". Nothing was saved.").ConfigureAwait(false);
                return;
            }

            var (result, saved) = await Task.Run(() => QuestEditorGate.Write(s => s.Save(dto, false))).ConfigureAwait(false);
            WebAdminAudit.Record(req, result.Created ? "quests.create" : "quests.save", "quest#" + result.Index.ToString(CultureInfo.InvariantCulture),
                new { index = result.Index, authorKey = dto.AuthorKey, steps = dto.Steps?.Count ?? 0, objectives = dto.Objectives?.Count ?? 0, warnings = result.Problems.Count }, Reason(body));
            await AfterSave(req, result, saved).ConfigureAwait(false);
            await req.Ok(result, (result.Created ? "Quest " + result.Index.ToString(CultureInfo.InvariantCulture) + " created" : "Quest saved") +
                                 " (" + Count(result.Problems) + "). " + result.Live?.Message).ConfigureAwait(false);
        }

        private static string Count(List<QuestProblemDto> problems)
        {
            var warnings = problems.Count(p => p.Warning);
            return warnings == 0 ? "no warnings" : warnings.ToString(CultureInfo.InvariantCulture) + " warning(s)";
        }

        private static async Task AfterSave(WebAdminRequest req, QuestSaveResult result, GameDataSave.SaveOutcome? saved)
        {
            try
            {
                // The LiveDb quest-replace hook: snapshot under EditLock, swap + online players' refresh on the game thread.
                result.Live = await QuestEditNotifier.QuestsChangedAsync(result.ChangedQuests, result.SharedTablesChanged, req.AdminName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WebAdminLog.Error(ex, "[Quests] The quest change notifier failed");
                WebAdminPending.Add("quests", "Quest definitions saved; the live apply failed (see the server log), loaded at the next world start", req.AdminName);
                result.Live = new QuestLiveOutcome { Message = "The live apply failed (see the server log): loaded at the next world start." };
            }

            // Main never reloads spells: the same follow-up as the other direct game-data saves (the editor copy's spells are unchanged).
            if (saved.HasValue) await GameDataSave.SyncMagicAsync(saved.Value.Magic).ConfigureAwait(false);
        }

        private static async Task PostDelete(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var rev = body.ValueKind == JsonValueKind.Object ? body.GetString("rev") : null;
            var (result, saved) = await Task.Run(() => QuestEditorGate.Write(s => s.Delete(index, rev))).ConfigureAwait(false);
            WebAdminAudit.Record(req, "quests.delete", "quest#" + index.ToString(CultureInfo.InvariantCulture), new { index }, Reason(body));
            await AfterSave(req, result, saved).ConfigureAwait(false);
            await req.Ok(result, "Quest " + index.ToString(CultureInfo.InvariantCulture) + " deleted. " + result.Live?.Message).ConfigureAwait(false);
        }

        private static async Task PostImport(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object) throw new WebAdminException(400, "Expected a JSON object body {file, apply?, token?}");
            var file = body.TryGetProperty("file", out var f) ? f.ValueKind == JsonValueKind.String ? f.GetString() : f.ValueKind == JsonValueKind.Object ? f.GetRawText() : null : null;
            if (string.IsNullOrWhiteSpace(file)) throw new WebAdminException(400, "'file' (the export file's text) is required");
            var apply = body.GetBool("apply");
            var token = body.GetString("token");

            if (!apply)
            {
                var preview = await Task.Run(() => QuestEditorGate.Read(s => s.Import(file, false, null))).ConfigureAwait(false);
                await req.Ok(preview, "Preview: " + preview.Diff.Count.ToString(CultureInfo.InvariantCulture) + " change line(s), " +
                                      preview.Problems.Count(p => !p.Warning).ToString(CultureInfo.InvariantCulture) + " error(s). Nothing was saved.").ConfigureAwait(false);
                return;
            }

            var (result, saved) = await Task.Run(() => QuestEditorGate.Write(s => s.Import(file, true, token))).ConfigureAwait(false);
            WebAdminAudit.Record(req, "quests.import", "quest#" + result.Index.ToString(CultureInfo.InvariantCulture), new { index = result.Index, mode = result.Created ? "create" : "update" }, Reason(body));
            await AfterSave(req, result, saved).ConfigureAwait(false);
            await req.Ok(result, "Imported into quest " + result.Index.ToString(CultureInfo.InvariantCulture) + ". " + result.Live?.Message).ConfigureAwait(false);
        }

        private static async Task PostGroupSave(WebAdminRequest req)
        {
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("group", out var g) || g.ValueKind != JsonValueKind.Object)
                throw new WebAdminException(400, "Expected a JSON object body {group}");
            QuestGroupDto dto;
            try
            {
                dto = g.Deserialize<QuestGroupDto>(WebAdminJson.Options);
            }
            catch (JsonException)
            {
                throw new WebAdminException(400, "'group' is not a valid group");
            }

            var (result, saved) = await Task.Run(() => QuestEditorGate.Write(s => s.SaveGroup(dto))).ConfigureAwait(false);
            WebAdminAudit.Record(req, "quests.group-save", "questgroup#" + result.Index.ToString(CultureInfo.InvariantCulture),
                new { index = result.Index, dto.AuthorKey, dto.Kind, members = dto.Members?.Count ?? 0 }, Reason(body));
            await AfterSave(req, result, saved).ConfigureAwait(false);
            await req.Ok(result, "Group saved. " + result.Live?.Message).ConfigureAwait(false);
        }

        private static async Task PostGroupDelete(WebAdminRequest req)
        {
            var index = req.RouteInt("index");
            var body = await req.ReadJsonAsync().ConfigureAwait(false);
            var (result, saved) = await Task.Run(() => QuestEditorGate.Write(s => s.DeleteGroup(index))).ConfigureAwait(false);
            WebAdminAudit.Record(req, "quests.group-delete", "questgroup#" + index.ToString(CultureInfo.InvariantCulture), new { index }, Reason(body));
            await AfterSave(req, result, saved).ConfigureAwait(false);
            await req.Ok(result, "Group deleted. " + result.Live?.Message).ConfigureAwait(false);
        }
    }
}
