// Quest V2 model check (src/game/quests.ts) over a recorded server sequence: login (definitions, Full states, progress,
// history), an NPC's availability, accept -> progress -> Ready -> turn in -> history, a share offer, a failed run, a
// history quest's definition request, the map change that clears the NPC entries, and (wave 3 kick-off) S.QuestRefused
// (pending lock + event), an NPC's Choosing quests, group target names (TargetMembers) and ShareMode. DOM-free; no
// server needed.
// Run: node --experimental-strip-types tools/quests-test.ts

import {
  QUEST_PENDING_MS, Quests, questRefusalText, type QuestHistoryRow, type QuestProgressChange, type QuestRefusedEvent, type QuestRunChange,
} from "../src/game/quests.ts";
import type { QuestDefinitionInfo, ServerPacket } from "../src/protocol/server.ts";
import type { ItemInfos } from "../src/game/items.ts";
import {
  ClientPacketIds, QuestCategory, QuestEndpointKind, QuestEndpointRole, QuestIcon, QuestObjectiveType, QuestRefusal, QuestRequestKind,
  QuestRewardKind, QuestRewardScope, QuestRunStatus, QuestShareMode, QuestTargetKind,
} from "../src/protocol/enums.generated.ts";

let failed = 0, passed = 0;
function check(name: string, ok: boolean, detail?: unknown): void {
  if (ok) passed++;
  else { failed++; console.error(`FAIL ${name}`, detail ?? ""); }
}

const sent: Uint8Array[] = [];
/** The client packet id of a sent frame (u16 little-endian after the u16 length). */
const idOf = (f: Uint8Array) => f[2]! | (f[3]! << 8);
const items = { get: () => undefined } as unknown as ItemInfos;
const q = new Quests((f) => sent.push(f), items);
q.namer = { item: (i) => (i === 7 ? "Hen Feather" : null), monster: (i) => (i === 3 ? "Hen" : null), npc: () => "Sarah", map: () => "Bichon" };
const feed = (p: ServerPacket) => q.handle(p);
const ms = (d: Date) => (BigInt(d.getTime()) * 10_000n + 621_355_968_000_000_000n) | (1n << 62n); // DateTime.ToBinary, Kind = Utc

const progress: QuestProgressChange[] = [], runs: QuestRunChange[] = [], done: QuestHistoryRow[] = [];
q.progressed.on((p) => progress.push(p));
q.runChanged.on((c) => runs.push(c));
q.completed.on((r) => done.push(r));

const def = (quest: number, extra: Partial<QuestDefinitionInfo> = {}): QuestDefinitionInfo => ({
  Quest: quest, Name: `Quest ${quest} name`, Summary: "Help the farm.", CompleteText: "Thanks!", Category: QuestCategory.Side, StorylineIndex: 0,
  LevelHint: 5, RepeatPolicy: 0, CooldownSeconds: 0, TimeLimitSeconds: 0, FailPolicy: 0, AbandonAllowed: true, Flags: 0, GroupName: "Farm", ShareMode: 1,
  Steps: [{ Index: 10, Order: 1, Title: "Hens", Text: "Kill hens.", ReadyText: "Go back.", CompletionMode: 0, NRequired: 0, NextStep: -1, TimeLimitSeconds: 0 }],
  Objectives: [
    { Index: 500 + quest, Step: 10, Order: 1, Type: QuestObjectiveType.Kill, TargetKind: QuestTargetKind.Monster, TargetId: 3, TargetName: "", Count: 5,
      Map: 0, X: 0, Y: 0, Radius: 0, Optional: false, Hidden: false, Text: "", TargetMembers: [] },
    { Index: 600 + quest, Step: 10, Order: 2, Type: QuestObjectiveType.Collect, TargetKind: QuestTargetKind.Item, TargetId: 7, TargetName: "", Count: 2,
      Map: 0, X: 0, Y: 0, Radius: 0, Optional: true, Hidden: false, Text: "", TargetMembers: [] },
  ],
  Rewards: [
    { Scope: QuestRewardScope.Quest, ScopeId: 0, Kind: QuestRewardKind.Exp, TargetId: 0, Amount: 1500n, ChoiceGroup: 0, FirstTimeOnly: false },
    { Scope: QuestRewardScope.Quest, ScopeId: 0, Kind: QuestRewardKind.Item, TargetId: 7, Amount: 1n, ChoiceGroup: 1, FirstTimeOnly: false },
    { Scope: QuestRewardScope.Quest, ScopeId: 0, Kind: QuestRewardKind.Item, TargetId: 8, Amount: 1n, ChoiceGroup: 1, FirstTimeOnly: false },
    { Scope: QuestRewardScope.Quest, ScopeId: 0, Kind: QuestRewardKind.Gold, TargetId: 0, Amount: 99n, ChoiceGroup: 0, FirstTimeOnly: true },
  ],
  Endpoints: [{ Role: QuestEndpointRole.Start, Kind: QuestEndpointKind.Npc, TargetId: 42, Name: "Merchant_Sarah", Map: 0 }],
  Branches: [],
  ...extra,
});

// — Login: UserInformation, map, a run in progress (definition first), its progress, an empty history —
feed({ type: "UserInformation", Name: "WebHero", Level: 7, Class: 0, Gender: 0, QuestInventory: [] } as unknown as ServerPacket);
feed({ type: "MapInformation", MapIndex: 1 } as unknown as ServerPacket);
feed({ type: "QuestDefinition", ...def(2) });
feed({ type: "QuestStates", Full: true, States: [{ Quest: 2, Status: QuestRunStatus.Active, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
feed({ type: "QuestProgress", Progress: [{ Quest: 2, Objective: 502, Value: 1 }, { Quest: 2, Objective: 602, Value: 0 }] });
feed({ type: "QuestHistory", Full: true, History: [] });
check("login run", q.run(2)?.status === QuestRunStatus.Active && q.runs().length === 1);
check("login no run events", runs.length === 0, runs);
check("login progress first values are not toasts", progress.every((p) => p.prev === undefined));
check("objective text", q.objectiveText(q.def(2)!.Objectives[0]!) === "Kill Hen: 1/5", q.objectiveText(q.def(2)!.Objectives[0]!));
check("collect text", q.objectiveText(q.def(2)!.Objectives[1]!) === "Collect Hen Feather: 0/2");
check("no definition request for a sent definition", sent.length === 0);

// — An NPC in view offers quest 1 —
feed({ type: "QuestDefinition", ...def(1) });
feed({ type: "QuestAvailability", Npcs: [{ ObjectID: 100, Icon: QuestIcon.ExclamationYellow, Offered: [1], Finishing: [], Choosing: [] }] });
check("npc icon", q.npcQuestIcon(100) === QuestIcon.ExclamationYellow);
check("npc has quests", q.npcHasQuests(100) && !q.npcHasQuests(101));
check("available offers", q.availableOffers().length === 1 && q.availableOffers()[0]!.npc === 100 && q.availableOffers()[0]!.map === 1);

// — Accept at the NPC: C.QuestAccept, then the server's state (TrackHint) and progress —
q.accept(1, 100);
check("accept frame", sent.length === 1 && idOf(sent[0]!) === ClientPacketIds.QuestAccept);
feed({ type: "QuestStates", Full: false, States: [{ Quest: 1, Status: QuestRunStatus.Active, Step: 10, ExpiresAt: 0n, StepExpiresAt: ms(new Date(Date.now() + 60_000)), TrackHint: true }] });
feed({ type: "QuestProgress", Progress: [{ Quest: 1, Objective: 501, Value: 0 }, { Quest: 1, Objective: 601, Value: 0 }] });
feed({ type: "QuestAvailability", Npcs: [{ ObjectID: 100, Icon: QuestIcon.QuestionWhite, Offered: [], Finishing: [1], Choosing: [] }] });
check("accept run event", runs.length === 1 && runs[0]!.prev === null && runs[0]!.run?.quest === 1);
check("track hint tracks", q.isTracked(1) && q.trackedRuns().some((r) => r.quest === 1));
check("taken offer leaves Available", q.availableOffers().length === 0);
const left = q.timeLeft(q.run(1)!);
check("step timer", left !== null && left > 55_000 && left <= 60_000, left);
check("in-progress icon", q.npcQuestIcon(100) === QuestIcon.QuestionWhite && q.finishingNpc(1) === 100);

// — Kills: one coalesced packet per tick —
progress.length = 0;
feed({ type: "QuestProgress", Progress: [{ Quest: 1, Objective: 501, Value: 3 }] });
check("progress toast event", progress.length === 1 && progress[0]!.prev === 0 && progress[0]!.value === 3 && progress[0]!.objective?.Index === 501);
check("value and fraction", q.value(q.def(1)!.Objectives[0]!) === 3 && Math.abs(q.fraction(q.def(1)!.Objectives[0]!) - 0.6) < 1e-9);
feed({ type: "QuestProgress", Progress: [{ Quest: 1, Objective: 501, Value: 5 }] });
check("step met (optional objective ignored)", q.stepMet(q.def(1)!, q.def(1)!.Steps[0]!));

// — Ready (server-derived), turn in with a pick from choice group 1 —
feed({ type: "QuestStates", Full: false, States: [{ Quest: 1, Status: QuestRunStatus.Ready, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
check("ready event", runs.length === 2 && runs[1]!.run?.status === QuestRunStatus.Ready);
check("choice groups", JSON.stringify(q.choiceGroups(q.def(1)!)) === "[1]");
check("no pick yet", q.picksFor(q.def(1)!) === null);
q.pick(1, 1, 2);
check("pick sent as a position", JSON.stringify(q.picksFor(q.def(1)!)) === "[2]");
q.turnIn(1, 100, q.picksFor(q.def(1)!)!);
check("turn-in frame", idOf(sent[sent.length - 1]!) === ClientPacketIds.QuestTurnIn);
feed({ type: "QuestStates", Full: false, States: [{ Quest: 1, Status: QuestRunStatus.None, Step: 0, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
const again = ms(new Date(Date.now() + 3_600_000));
feed({ type: "QuestHistory", Full: false, History: [{ Quest: 1, Name: "Quest 1 name", Times: 1, LastAt: ms(new Date()), AvailableAt: again }] });
check("run gone + untracked", !q.run(1) && !q.isTracked(1));
check("end event", runs.length === 3 && runs[2]!.run === null);
check("completed event", done.length === 1 && done[0]!.quest === 1);
check("repeat timer", Math.abs(q.history(1)!.availableAt - (Date.now() + 3_600_000)) < 2000);
check("first-time reward hidden after a completion", q.rewards(q.def(1)!).every((r) => !r.reward.FirstTimeOnly));
check("picks cleared", q.pickOf(1, 1) === -1);

// — A share offer: accept with NpcObjectID 0 drops the prompt —
feed({ type: "QuestDefinition", ...def(3) });
feed({ type: "QuestOffer", Quest: 3, Kind: QuestEndpointKind.Npc, SharedBy: "WebAlly" });
check("offer prompt", q.offers.length === 1 && q.offers[0]!.sharedBy === "WebAlly");
q.accept(3, 0);
check("offer accept frame + prompt gone", idOf(sent[sent.length - 1]!) === ClientPacketIds.QuestAccept && q.offers.length === 0);

// — A failed run (FailPolicy Fail) stays in the list and does not count against the cap —
feed({ type: "QuestStates", Full: false, States: [{ Quest: 2, Status: QuestRunStatus.Failed, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
check("failed run", q.run(2)?.status === QuestRunStatus.Failed && q.activeCount === 0);

// — A history quest without a definition: C.QuestDefinitionRequest once —
feed({ type: "QuestHistory", Full: false, History: [{ Quest: 9, Name: "Old quest", Times: 2, LastAt: ms(new Date()), AvailableAt: 0n }] });
const before = sent.length;
q.requestDefinition(9); q.requestDefinition(9);
check("definition request once", sent.length === before + 1 && idOf(sent[sent.length - 1]!) === ClientPacketIds.QuestDefinitionRequest);
check("history name without a definition", q.name(9) === "Old quest");

// — Map change: the server re-sends availability, so the entries go —
feed({ type: "MapChanged", MapIndex: 2 } as unknown as ServerPacket);
check("map change clears npc entries", q.npcQuestIcon(100) === null && !q.npcHasQuests(100));

// — A Full states list (relog / class change) replaces the runs —
feed({ type: "QuestStates", Full: true, States: [] });
check("full replace", q.runs().length === 0);

// — S.QuestRefused: a refused turn-in lifts the pending lock and fires `refused` —
const refusals: QuestRefusedEvent[] = [];
q.refused.on((e) => refusals.push(e));
feed({ type: "QuestDefinition", ...def(4, { ShareMode: QuestShareMode.None }) });
feed({ type: "QuestStates", Full: false, States: [{ Quest: 4, Status: QuestRunStatus.Active, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
q.turnIn(4, 100, []);
check("turn-in locks the quest", q.isPending(4) && !q.isPending(5));
check("lock times out", !q.isPending(4, Date.now() + QUEST_PENDING_MS + 1));
feed({ type: "QuestRefused", Quest: 4, Request: QuestRequestKind.TurnIn, Reason: QuestRefusal.NotReady });
check("refusal unlocks", !q.isPending(4));
check("refused event", refusals.length === 1 && refusals[0]!.quest === 4 && refusals[0]!.request === QuestRequestKind.TurnIn && refusals[0]!.reason === QuestRefusal.NotReady, refusals);
const rt = questRefusalText(refusals[0]!, q.name(4));
check("refusal text", rt.title === "Cannot hand in Quest 4 name" && rt.reason === "The quest is not ready to hand in.", rt);
check("refusal text fallback", questRefusalText({ quest: 4, request: QuestRequestKind.Choose, reason: 200 }, "x").reason === "The request was refused.");
q.abandon(4);
feed({ type: "QuestStates", Full: false, States: [{ Quest: 4, Status: QuestRunStatus.Active, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
check("a state answer unlocks", !q.isPending(4));

// — ShareMode: None never shares; Offer shares only in a group —
check("share mode None", !q.canShare(4, 3));
feed({ type: "QuestDefinition", ...def(6) });
check("share mode Offer needs a group", q.canShare(6, 2) && !q.canShare(6, 1) && !q.canShare(999, 2));

// — Choosing: a running quest whose step branches can be chosen at the NPC (listed met or not) —
feed({ type: "QuestDefinition", ...def(5, { Branches: [{ Step: 10, ChoiceKey: "left", Text: "Go left", Outcome: 0 }, { Step: 10, ChoiceKey: "right", Text: "Go right", Outcome: 0 }] }) });
feed({ type: "QuestStates", Full: false, States: [{ Quest: 5, Status: QuestRunStatus.Active, Step: 10, ExpiresAt: 0n, StepExpiresAt: 0n, TrackHint: false }] });
feed({ type: "QuestAvailability", Npcs: [{ ObjectID: 200, Icon: QuestIcon.QuestionWhite, Offered: [6], Finishing: [], Choosing: [5] }, { ObjectID: 201, Icon: QuestIcon.None, Offered: [], Finishing: [], Choosing: [5] }] });
check("choosing makes npcHasQuests", q.npcHasQuests(201) && JSON.stringify(q.npcChoosing(201)) === "[5]");
check("choosing listed before offers", JSON.stringify(q.npcQuestList(200)) === "[5,6]", q.npcQuestList(200));
check("step branches before met", q.stepBranches(5).length === 2 && q.pendingBranches(5).length === 0);
feed({ type: "QuestProgress", Progress: [{ Quest: 5, Objective: 505, Value: 5 }] });
check("branches once met", q.pendingBranches(5).length === 2);
q.choose(5, 201, "left");
check("choose frame + lock", idOf(sent[sent.length - 1]!) === ClientPacketIds.QuestChoose && q.isPending(5));

// — Group targets: named by their members (at most 3, then "…") once the names are known —
const groupObj = (members: number[], kind: number = QuestTargetKind.MonsterGroup) => ({
  Index: 900, Step: 10, Order: 1, Type: QuestObjectiveType.Kill, TargetKind: kind, TargetId: 1, TargetName: "Hens", Count: 10,
  Map: 0, X: 0, Y: 0, Radius: 0, Optional: false, Hidden: false, Text: "", TargetMembers: members,
});
const MONSTERS = ["", "", "", "Hen", "Rooster", "Chick", "Duck"];
q.namer = { item: (i) => (i === 7 ? "Hen Feather" : i === 8 ? "Egg" : null), monster: (i) => MONSTERS[i] || null, npc: () => "Sarah", map: () => "Bichon" };
check("group members named", q.objectiveText(groupObj([3, 4])) === "Kill Hen, Rooster: 0/10", q.objectiveText(groupObj([3, 4])));
check("group members capped", q.objectiveText(groupObj([3, 4, 5, 6])) === "Kill Hen, Rooster, Chick, …: 0/10", q.objectiveText(groupObj([3, 4, 5, 6])));
check("group with an unknown member keeps TargetName", q.objectiveText(groupObj([3, 77])) === "Kill Hens (any): 0/10", q.objectiveText(groupObj([3, 77])));
check("one-member group keeps TargetName", q.objectiveText(groupObj([3])) === "Kill Hens (any): 0/10");
check("item group members", q.targetName(groupObj([7, 8], QuestTargetKind.ItemGroup)) === "Hen Feather, Egg");


console.log(`quests-test: ${passed} passed, ${failed} failed`);
if (failed) process.exit(1);
