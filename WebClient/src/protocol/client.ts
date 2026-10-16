// Browser -> server packets: hand-ported from Shared/ClientPackets.cs WritePacket bodies (same field order and types).
// Goldens: test-data/codec/client.*.json (WebRelay --emit-fixtures), checked by tools/codec-test.ts.
// Rule: a change to one of these packets in Shared/ClientPackets.cs must update this file and the fixture sample.
// A malformed client packet gets the RELAY's session closed (and would IP-block a desktop client), so keep these exact.

import { Writer, framePacket } from "./binaryCodec.ts";
import { ClientPacketIds as C } from "./enums.generated.ts";
import type { ClientIntelligentCreature } from "./server.ts";

/** ClientIntelligentCreature.Save (Shared/Data/IntelligentCreatureData.cs): the reader's layout. Int64s go through
 *  BigInt() so the codec test's JSON numbers / strings also encode. */
function writeCreature(w: Writer, c: ClientIntelligentCreature): void {
  w.writeU8(c.PetType); w.writeI32(c.Icon); w.writeString(c.CustomName); w.writeI32(c.Fullness); w.writeI32(c.SlotIndex);
  w.writeI64(BigInt(c.Expire)); w.writeI64(BigInt(c.BlackstoneTime)); w.writeU8(c.petMode);
  const r = c.CreatureRules;
  w.writeI32(r.MinimalFullness); w.writeBool(r.MousePickupEnabled); w.writeI32(r.MousePickupRange); w.writeBool(r.AutoPickupEnabled);
  w.writeI32(r.AutoPickupRange); w.writeBool(r.SemiAutoPickupEnabled); w.writeI32(r.SemiAutoPickupRange); w.writeBool(r.CanProduceBlackStone);
  const f = c.Filter;
  w.writeBool(f.PetPickupAll); w.writeBool(f.PetPickupGold); w.writeBool(f.PetPickupWeapons); w.writeBool(f.PetPickupArmours); w.writeBool(f.PetPickupHelmets);
  w.writeBool(f.PetPickupBoots); w.writeBool(f.PetPickupBelts); w.writeBool(f.PetPickupAccessories); w.writeBool(f.PetPickupOthers);
  w.writeU8(f.PickupGrade);
  w.writeI64(BigInt(c.MaintainFoodTime));
}

function frame(id: number, body?: (w: Writer) => void): Uint8Array {
  const w = new Writer(64);
  body?.(w);
  return framePacket(id, w.toUint8Array());
}

export const ClientPackets = {
  /** The relay replaces the hash with the MD5 of its --version-path, so any 16 bytes do. */
  ClientVersion: () => frame(C.ClientVersion, (w) => w.writeBytes(new Uint8Array(16))),
  KeepAlive: (time: bigint) => frame(C.KeepAlive, (w) => w.writeI64(time)),
  Login: (p: { AccountID: string; Password: string }) => frame(C.Login, (w) => {
    w.writeString(p.AccountID);
    w.writeString(p.Password);
  }),
  NewAccount: (p: { AccountID: string; Password: string; BirthDate: bigint; UserName: string; SecretQuestion: string; SecretAnswer: string; EMailAddress: string }) =>
    frame(C.NewAccount, (w) => {
      w.writeString(p.AccountID);
      w.writeString(p.Password);
      w.writeI64(p.BirthDate);
      w.writeString(p.UserName);
      w.writeString(p.SecretQuestion);
      w.writeString(p.SecretAnswer);
      w.writeString(p.EMailAddress);
    }),
  NewCharacter: (p: { Name: string; Gender: number; Class: number }) => frame(C.NewCharacter, (w) => {
    w.writeString(p.Name);
    w.writeU8(p.Gender);
    w.writeU8(p.Class);
  }),
  StartGame: (p: { CharacterIndex: number }) => frame(C.StartGame, (w) => w.writeI32(p.CharacterIndex)),
  LogOut: () => frame(C.LogOut),
  Turn: (p: { Direction: number }) => frame(C.Turn, (w) => w.writeU8(p.Direction)),
  Walk: (p: { Direction: number }) => frame(C.Walk, (w) => w.writeU8(p.Direction)),
  Run: (p: { Direction: number }) => frame(C.Run, (w) => w.writeU8(p.Direction)),
  Attack: (p: { Direction: number; Spell: number }) => frame(C.Attack, (w) => {
    w.writeU8(p.Direction);
    w.writeU8(p.Spell);
  }),
  /** MapControl.CheckDoorOpen: ask the server to open a closed door. */
  Opendoor: (p: { DoorIndex: number }) => frame(C.Opendoor, (w) => w.writeU8(p.DoorIndex)),
  // — Group (GroupDialog) —
  SwitchGroup: (p: { AllowGroup: boolean }) => frame(C.SwitchGroup, (w) => w.writeBool(p.AllowGroup)),
  AddMember: (p: { Name: string }) => frame(C.AddMember, (w) => w.writeString(p.Name)),
  /** ClientPacketIds.DellMember (sic). */
  DelMember: (p: { Name: string }) => frame(C.DellMember, (w) => w.writeString(p.Name)),
  GroupInvite: (p: { AcceptInvite: boolean }) => frame(C.GroupInvite, (w) => w.writeBool(p.AcceptInvite)),
  // — Magic —
  Magic: (p: { ObjectID: number; Spell: number; Direction: number; TargetID: number; Location: { X: number; Y: number }; SpellTargetLock: boolean }) => frame(C.Magic, (w) => {
    w.writeU32(p.ObjectID); w.writeU8(p.Spell); w.writeU8(p.Direction); w.writeU32(p.TargetID); w.writeI32(p.Location.X); w.writeI32(p.Location.Y); w.writeBool(p.SpellTargetLock);
  }),
  MagicKey: (p: { Spell: number; Key: number; OldKey: number }) => frame(C.MagicKey, (w) => { w.writeU8(p.Spell); w.writeU8(p.Key); w.writeU8(p.OldKey); }),
  /** canUse is SpellToggleState (sbyte): -1 None, 0 False, 1 True. */
  SpellToggle: (p: { Spell: number; canUse: number }) => frame(C.SpellToggle, (w) => { w.writeU8(p.Spell); w.writeI8(p.canUse); }),
  // — NPCs —
  /** Key is "[@Main]" for a fresh talk, "[@action]" for a dialog link (NPCDialog.ButtonClicked). */
  CallNPC: (p: { ObjectID: number; Key: string }) => frame(C.CallNPC, (w) => { w.writeU32(p.ObjectID); w.writeString(p.Key); }),
  /** ItemIndex is the goods entry's UniqueID (NPCGoodsDialog.BuyItem). */
  BuyItem: (p: { ItemIndex: bigint; Count: number; Type: number }) => frame(C.BuyItem, (w) => { w.writeU64(p.ItemIndex); w.writeU16(p.Count); w.writeU8(p.Type); }),
  SellItem: (p: { UniqueID: bigint; Count: number }) => frame(C.SellItem, (w) => { w.writeU64(p.UniqueID); w.writeU16(p.Count); }),
  RepairItem: (p: { UniqueID: bigint }) => frame(C.RepairItem, (w) => w.writeU64(p.UniqueID)),
  SRepairItem: (p: { UniqueID: bigint }) => frame(C.SRepairItem, (w) => w.writeU64(p.UniqueID)),
  /** Pick up whatever lies on the user's cell (no fields). */
  PickUp: () => frame(C.PickUp),
  // — Items (MirItemCell) —
  MoveItem: (p: { Grid: number; From: number; To: number }) => frame(C.MoveItem, (w) => { w.writeU8(p.Grid); w.writeI32(p.From); w.writeI32(p.To); }),
  EquipItem: (p: { Grid: number; UniqueID: bigint; To: number }) => frame(C.EquipItem, (w) => { w.writeU8(p.Grid); w.writeU64(p.UniqueID); w.writeI32(p.To); }),
  RemoveItem: (p: { Grid: number; UniqueID: bigint; To: number }) => frame(C.RemoveItem, (w) => { w.writeU8(p.Grid); w.writeU64(p.UniqueID); w.writeI32(p.To); }),
  MergeItem: (p: { GridFrom: number; GridTo: number; IDFrom: bigint; IDTo: bigint }) => frame(C.MergeItem, (w) => {
    w.writeU8(p.GridFrom); w.writeU8(p.GridTo); w.writeU64(p.IDFrom); w.writeU64(p.IDTo);
  }),
  UseItem: (p: { UniqueID: bigint; Grid: number }) => frame(C.UseItem, (w) => { w.writeU64(p.UniqueID); w.writeU8(p.Grid); }),
  DropItem: (p: { UniqueID: bigint; Count: number; HeroInventory: boolean }) => frame(C.DropItem, (w) => { w.writeU64(p.UniqueID); w.writeU16(p.Count); w.writeBool(p.HeroInventory); }),
  // — Trade (TradeDialog / MirItemCell) —
  /** Ask the player you face (KeybindOptions.Trade). */
  TradeRequest: () => frame(C.TradeRequest),
  TradeReply: (p: { AcceptInvite: boolean }) => frame(C.TradeReply, (w) => w.writeBool(p.AcceptInvite)),
  TradeGold: (p: { Amount: number }) => frame(C.TradeGold, (w) => w.writeU32(p.Amount)),
  TradeConfirm: (p: { Locked: boolean }) => frame(C.TradeConfirm, (w) => w.writeBool(p.Locked)),
  TradeCancel: () => frame(C.TradeCancel),
  /** Inventory slot -> trade slot. */
  DepositTradeItem: (p: { From: number; To: number }) => frame(C.DepositTradeItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  /** Trade slot -> inventory slot. */
  RetrieveTradeItem: (p: { From: number; To: number }) => frame(C.RetrieveTradeItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  // — Guild (GuildDialog) —
  /** ChangeType: 0 recruit, 1 kick, 2 change member rank, 3 rename rank, 4 new rank, 5 rank option. */
  EditGuildMember: (p: { ChangeType: number; RankIndex: number; Name: string; RankName: string }) => frame(C.EditGuildMember, (w) => {
    w.writeU8(p.ChangeType); w.writeU8(p.RankIndex); w.writeString(p.Name); w.writeString(p.RankName);
  }),
  EditGuildNotice: (p: { notice: string[] }) => frame(C.EditGuildNotice, (w) => { w.writeI32(p.notice.length); for (const l of p.notice) w.writeString(l); }),
  GuildInvite: (p: { AcceptInvite: boolean }) => frame(C.GuildInvite, (w) => w.writeBool(p.AcceptInvite)),
  /** Type 0 = notice, 1 = member list. */
  RequestGuildInfo: (p: { Type: number }) => frame(C.RequestGuildInfo, (w) => w.writeU8(p.Type)),
  /** Type 0 = donate, 1 = retrieve (leader rank only). */
  GuildStorageGoldChange: (p: { Type: number; Amount: number }) => frame(C.GuildStorageGoldChange, (w) => { w.writeU8(p.Type); w.writeU32(p.Amount); }),
  /** Type 0 = store (inventory From -> storage To), 1 = retrieve (storage From -> inventory To), 2 = move in storage, 3 = request the list. */
  GuildStorageItemChange: (p: { Type: number; From: number; To: number }) => frame(C.GuildStorageItemChange, (w) => { w.writeU8(p.Type); w.writeI32(p.From); w.writeI32(p.To); }),
  /** Action 0 = request the buff list, 1 = buy (enable) a buff, 2 = activate a timed buff. */
  GuildBuffUpdate: (p: { Action: number; Id: number }) => frame(C.GuildBuffUpdate, (w) => { w.writeU8(p.Action); w.writeI32(p.Id); }),
  /** The answer to S.GuildRequestWar: the enemy guild's name (leader rank only). */
  GuildWarReturn: (p: { Name: string }) => frame(C.GuildWarReturn, (w) => w.writeString(p.Name)),
  GuildNameReturn: (p: { Name: string }) => frame(C.GuildNameReturn, (w) => w.writeString(p.Name)),
  /** LinkedItems: ChatItem (UniqueID, Title, Grid) for each "<Title/UniqueID>" link in the message (ChatDialog.LinkedItems). */
  Chat: (p: { Message: string; LinkedItems?: { UniqueID: bigint; Title: string; Grid: number }[] }) => frame(C.Chat, (w) => {
    w.writeString(p.Message);
    const items = p.LinkedItems ?? [];
    w.writeI32(items.length);
    for (const i of items) { w.writeU64(BigInt(i.UniqueID)); w.writeString(i.Title); w.writeU8(i.Grid); }
  }),
  // — Section 4: social —
  AcceptQuest: (p: { NPCIndex: number; QuestIndex: number }) => frame(C.AcceptQuest, (w) => { w.writeU32(p.NPCIndex); w.writeI32(p.QuestIndex); }),
  /** SelectedItemIndex: the chosen RewardsSelectItem entry, -1 for none. */
  FinishQuest: (p: { QuestIndex: number; SelectedItemIndex: number }) => frame(C.FinishQuest, (w) => { w.writeI32(p.QuestIndex); w.writeI32(p.SelectedItemIndex); }),
  AbandonQuest: (p: { QuestIndex: number }) => frame(C.AbandonQuest, (w) => w.writeI32(p.QuestIndex)),
  ShareQuest: (p: { QuestIndex: number }) => frame(C.ShareQuest, (w) => w.writeI32(p.QuestIndex)),
  /** ItemsIdx: always 5 UniqueIDs (0 = empty slot). */
  SendMail: (p: { Name: string; Message: string; Gold: number; ItemsIdx: bigint[]; Stamped: boolean }) => frame(C.SendMail, (w) => {
    w.writeString(p.Name); w.writeString(p.Message); w.writeU32(p.Gold);
    for (let i = 0; i < 5; i++) w.writeU64(BigInt(p.ItemsIdx[i] ?? 0n));
    w.writeBool(p.Stamped);
  }),
  ReadMail: (p: { MailID: bigint }) => frame(C.ReadMail, (w) => w.writeU64(p.MailID)),
  CollectParcel: (p: { MailID: bigint }) => frame(C.CollectParcel, (w) => w.writeU64(p.MailID)),
  DeleteMail: (p: { MailID: bigint }) => frame(C.DeleteMail, (w) => w.writeU64(p.MailID)),
  LockMail: (p: { MailID: bigint; Lock: boolean }) => frame(C.LockMail, (w) => { w.writeU64(p.MailID); w.writeBool(p.Lock); }),
  MailLockedItem: (p: { UniqueID: bigint; Locked: boolean }) => frame(C.MailLockedItem, (w) => { w.writeU64(p.UniqueID); w.writeBool(p.Locked); }),
  MailCost: (p: { Gold: number; ItemsIdx: bigint[]; Stamped: boolean }) => frame(C.MailCost, (w) => {
    w.writeU32(p.Gold);
    for (let i = 0; i < 5; i++) w.writeU64(BigInt(p.ItemsIdx[i] ?? 0n));
    w.writeBool(p.Stamped);
  }),
  /** Blocked = false sends a friend request (target online; S.FriendRequest to them); Blocked = true blocks at once (one-sided).
   *  RemoveFriend of a non-blocked friend removes both sides. */
  AddFriend: (p: { Name: string; Blocked: boolean }) => frame(C.AddFriend, (w) => { w.writeString(p.Name); w.writeBool(p.Blocked); }),
  RemoveFriend: (p: { CharacterIndex: number }) => frame(C.RemoveFriend, (w) => w.writeI32(p.CharacterIndex)),
  RefreshFriends: () => frame(C.RefreshFriends),
  AddMemo: (p: { CharacterIndex: number; Memo: string }) => frame(C.AddMemo, (w) => { w.writeI32(p.CharacterIndex); w.writeString(p.Memo); }),
  MarriageRequest: () => frame(C.MarriageRequest),
  MarriageReply: (p: { AcceptInvite: boolean }) => frame(C.MarriageReply, (w) => w.writeBool(p.AcceptInvite)),
  ChangeMarriage: () => frame(C.ChangeMarriage),
  DivorceRequest: () => frame(C.DivorceRequest),
  DivorceReply: (p: { AcceptInvite: boolean }) => frame(C.DivorceReply, (w) => w.writeBool(p.AcceptInvite)),
  AddMentor: (p: { Name: string }) => frame(C.AddMentor, (w) => w.writeString(p.Name)),
  MentorReply: (p: { AcceptInvite: boolean }) => frame(C.MentorReply, (w) => w.writeBool(p.AcceptInvite)),
  AllowMentor: () => frame(C.AllowMentor),
  CancelMentor: () => frame(C.CancelMentor),
  /** Ranking: inspect from the ranking list (ObjectID = the character's PlayerId); Hero: inspect a hero. */
  Inspect: (p: { ObjectID: number; Ranking: boolean; Hero: boolean }) => frame(C.Inspect, (w) => { w.writeU32(p.ObjectID); w.writeBool(p.Ranking); w.writeBool(p.Hero); }),
  Observe: (p: { Name: string }) => frame(C.Observe, (w) => w.writeString(p.Name)),
  GetRanking: (p: { RankType: number; RankIndex: number; OnlineOnly: boolean }) => frame(C.GetRanking, (w) => { w.writeU8(p.RankType); w.writeI32(p.RankIndex); w.writeBool(p.OnlineOnly); }),
  // — Section 3: NPC services —
  StoreItem: (p: { From: number; To: number }) => frame(C.StoreItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  TakeBackItem: (p: { From: number; To: number }) => frame(C.TakeBackItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  UnlockStorage: (p: { Password: string }) => frame(C.UnlockStorage, (w) => w.writeString(p.Password)),
  SetStoragePassword: (p: { CurrentPassword: string; NewPassword: string }) => frame(C.SetStoragePassword, (w) => { w.writeString(p.CurrentPassword); w.writeString(p.NewPassword); }),
  BuyItemBack: (p: { UniqueID: bigint; Count: number }) => frame(C.BuyItemBack, (w) => { w.writeU64(p.UniqueID); w.writeU16(p.Count); }),
  DepositRefineItem: (p: { From: number; To: number }) => frame(C.DepositRefineItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  RetrieveRefineItem: (p: { From: number; To: number }) => frame(C.RetrieveRefineItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  RefineCancel: () => frame(C.RefineCancel),
  RefineItem: (p: { UniqueID: bigint }) => frame(C.RefineItem, (w) => w.writeU64(p.UniqueID)),
  CheckRefine: (p: { UniqueID: bigint }) => frame(C.CheckRefine, (w) => w.writeU64(p.UniqueID)),
  ReplaceWedRing: (p: { UniqueID: bigint }) => frame(C.ReplaceWedRing, (w) => w.writeU64(p.UniqueID)),
  /** Slots: the inventory slots of the ingredients (CraftDialog). */
  CraftItem: (p: { UniqueID: bigint; Count: number; Slots: number[] }) => frame(C.CraftItem, (w) => {
    w.writeU64(p.UniqueID); w.writeU16(p.Count); w.writeI32(p.Slots.length); for (const s of p.Slots) w.writeI32(s);
  }),
  /** Type: AwakeType. */
  AwakeningNeedMaterials: (p: { UniqueID: bigint; Type: number }) => frame(C.AwakeningNeedMaterials, (w) => { w.writeU64(p.UniqueID); w.writeU8(p.Type); }),
  AwakeningLockedItem: (p: { UniqueID: bigint; Locked: boolean }) => frame(C.AwakeningLockedItem, (w) => { w.writeU64(p.UniqueID); w.writeBool(p.Locked); }),
  Awakening: (p: { UniqueID: bigint; Type: number; PositionIdx: number }) => frame(C.Awakening, (w) => { w.writeU64(p.UniqueID); w.writeU8(p.Type); w.writeU32(p.PositionIdx); }),
  DisassembleItem: (p: { UniqueID: bigint }) => frame(C.DisassembleItem, (w) => w.writeU64(p.UniqueID)),
  DowngradeAwakening: (p: { UniqueID: bigint }) => frame(C.DowngradeAwakening, (w) => w.writeU64(p.UniqueID)),
  ResetAddedItem: (p: { UniqueID: bigint }) => frame(C.ResetAddedItem, (w) => w.writeU64(p.UniqueID)),
  /** Type: MarketPanelType (Consign / Auction). */
  ConsignItem: (p: { UniqueID: bigint; Price: number; Type: number }) => frame(C.ConsignItem, (w) => { w.writeU64(p.UniqueID); w.writeU32(p.Price); w.writeU8(p.Type); }),
  MarketSearch: (p: { Match: string; Type: number; Usermode: boolean; MinShape: number; MaxShape: number; MarketType: number }) => frame(C.MarketSearch, (w) => {
    w.writeString(p.Match); w.writeU8(p.Type); w.writeBool(p.Usermode); w.writeI16(p.MinShape); w.writeI16(p.MaxShape); w.writeU8(p.MarketType);
  }),
  MarketRefresh: () => frame(C.MarketRefresh),
  MarketPage: (p: { Page: number }) => frame(C.MarketPage, (w) => w.writeI32(p.Page)),
  MarketBuy: (p: { AuctionID: bigint; BidPrice: number }) => frame(C.MarketBuy, (w) => { w.writeU64(p.AuctionID); w.writeU32(p.BidPrice); }),
  /** Mode: MarketCollectionMode. */
  MarketGetBack: (p: { Mode: number; AuctionID: bigint }) => frame(C.MarketGetBack, (w) => { w.writeU8(p.Mode); w.writeU64(p.AuctionID); }),
  MarketSellNow: (p: { AuctionID: bigint }) => frame(C.MarketSellNow, (w) => w.writeU64(p.AuctionID)),
  RemoveSlotItem: (p: { Grid: number; GridTo: number; UniqueID: bigint; To: number; FromUniqueID: bigint }) => frame(C.RemoveSlotItem, (w) => {
    w.writeU8(p.Grid); w.writeU8(p.GridTo); w.writeU64(p.UniqueID); w.writeI32(p.To); w.writeU64(p.FromUniqueID);
  }),
  EquipSlotItem: (p: { Grid: number; UniqueID: bigint; To: number; GridTo: number; ToUniqueID: bigint }) => frame(C.EquipSlotItem, (w) => {
    w.writeU8(p.Grid); w.writeU64(p.UniqueID); w.writeI32(p.To); w.writeU8(p.GridTo); w.writeU64(p.ToUniqueID);
  }),
  CombineItem: (p: { Grid: number; IDFrom: bigint; IDTo: bigint }) => frame(C.CombineItem, (w) => { w.writeU8(p.Grid); w.writeU64(p.IDFrom); w.writeU64(p.IDTo); }),
  ItemRentalRequest: () => frame(C.ItemRentalRequest),
  ItemRentalFee: (p: { Amount: number }) => frame(C.ItemRentalFee, (w) => w.writeU32(p.Amount)),
  ItemRentalPeriod: (p: { Days: number }) => frame(C.ItemRentalPeriod, (w) => w.writeU32(p.Days)),
  DepositRentalItem: (p: { From: number; To: number }) => frame(C.DepositRentalItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  RetrieveRentalItem: (p: { From: number; To: number }) => frame(C.RetrieveRentalItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  CancelItemRental: () => frame(C.CancelItemRental),
  ItemRentalLockFee: () => frame(C.ItemRentalLockFee),
  ItemRentalLockItem: () => frame(C.ItemRentalLockItem),
  ConfirmItemRental: () => frame(C.ConfirmItemRental),
  /** ItemRentalDialog.RequestRentedItems (at most once a minute). */
  GetRentedItems: () => frame(C.GetRentedItems),
  // — Section 2: class moves and info —
  /** MirAction.Harvest (Alt+click on a corpse). */
  Harvest: (p: { Direction: number }) => frame(C.Harvest, (w) => w.writeU8(p.Direction)),
  /** MirAction.AttackRange1 (Archer bow shot). */
  RangeAttack: (p: { Direction: number; Location: { X: number; Y: number }; TargetID: number; TargetLocation: { X: number; Y: number } }) => frame(C.RangeAttack, (w) => {
    w.writeU8(p.Direction); w.writeI32(p.Location.X); w.writeI32(p.Location.Y); w.writeU32(p.TargetID); w.writeI32(p.TargetLocation.X); w.writeI32(p.TargetLocation.Y);
  }),
  RequestMapInfo: (p: { MapIndex: number }) => frame(C.RequestMapInfo, (w) => w.writeI32(p.MapIndex)),
  RequestMonsterInfo: (p: { MonsterIndex: number }) => frame(C.RequestMonsterInfo, (w) => w.writeI32(p.MonsterIndex)),
  RequestNPCInfo: (p: { NPCIndex: number }) => frame(C.RequestNPCInfo, (w) => w.writeI32(p.NPCIndex)),
  /** C# int (Number() also accepts the codec test's bigint). */
  RequestItemInfo: (p: { ItemIndex: number }) => frame(C.RequestItemInfo, (w) => w.writeI32(Number(p.ItemIndex))),
  SearchMap: (p: { Text: string }) => frame(C.SearchMap, (w) => w.writeString(p.Text)),
  TeleportToNPC: (p: { ObjectID: number }) => frame(C.TeleportToNPC, (w) => w.writeU32(p.ObjectID)),
  // — Section 1: basic play —
  TownRevive: () => frame(C.TownRevive),
  SplitItem: (p: { Grid: number; UniqueID: bigint; Count: number }) => frame(C.SplitItem, (w) => { w.writeU8(p.Grid); w.writeU64(p.UniqueID); w.writeU16(p.Count); }),
  DropGold: (p: { Amount: number }) => frame(C.DropGold, (w) => w.writeU32(p.Amount)),
  ChangeAMode: (p: { Mode: number }) => frame(C.ChangeAMode, (w) => w.writeU8(p.Mode)),
  ChangePMode: (p: { Mode: number }) => frame(C.ChangePMode, (w) => w.writeU8(p.Mode)),
  ChangePassword: (p: { AccountID: string; CurrentPassword: string; NewPassword: string }) => frame(C.ChangePassword, (w) => {
    w.writeString(p.AccountID); w.writeString(p.CurrentPassword); w.writeString(p.NewPassword);
  }),
  DeleteCharacter: (p: { CharacterIndex: number }) => frame(C.DeleteCharacter, (w) => w.writeI32(p.CharacterIndex)),
  NPCConfirmInput: (p: { NPCID: number; PageName: string; Value: string }) => frame(C.NPCConfirmInput, (w) => {
    w.writeU32(p.NPCID); w.writeString(p.PageName); w.writeString(p.Value);
  }),
  RequestChatItem: (p: { ChatItemID: bigint }) => frame(C.RequestChatItem, (w) => w.writeU64(p.ChatItemID)),
  // — Section 5: fishing, reincarnation, game shop, guild territories —
  /** CastOut = true: the map click with a rod (GameScene CanFish); false: reel in (FishingStatusDialog). */
  FishingCast: (p: { CastOut: boolean }) => frame(C.FishingCast, (w) => w.writeBool(p.CastOut)),
  FishingChangeAutocast: (p: { AutoCast: boolean }) => frame(C.FishingChangeAutocast, (w) => w.writeBool(p.AutoCast)),
  /** The revive question after S.RequestReincarnation. */
  AcceptReincarnation: () => frame(C.AcceptReincarnation),
  /** GameScene.Struck while the user is casting Reincarnation (ReincarnationStopTime). */
  CancelReincarnation: () => frame(C.CancelReincarnation),
  /** PType: 0 = credit, 1 = gold (GameShopViewer.BuyItem). */
  GameshopBuy: (p: { GIndex: number; Quantity: number; PType: number }) => frame(C.GameshopBuy, (w) => { w.writeI32(p.GIndex); w.writeU8(p.Quantity); w.writeI32(p.PType); }),
  GuildTerritoryPage: (p: { Page: number }) => frame(C.GuildTerritoryPage, (w) => w.writeI32(p.Page)),
  // — Heroes —
  /** Hero bag/belt (From, HeroBeltIdx-based) -> user bag/belt (To, BeltIdx-based). */
  TakeBackHeroItem: (p: { From: number; To: number }) => frame(C.TakeBackHeroItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  /** User bag/belt (From) -> hero bag/belt (To). */
  TransferHeroItem: (p: { From: number; To: number }) => frame(C.TransferHeroItem, (w) => { w.writeI32(p.From); w.writeI32(p.To); }),
  NewHero: (p: { Name: string; Gender: number; Class: number }) => frame(C.NewHero, (w) => { w.writeString(p.Name); w.writeU8(p.Gender); w.writeU8(p.Class); }),
  /** Stat.HP or Stat.MP; Value = percent. */
  SetAutoPotValue: (p: { Stat: number; Value: number }) => frame(C.SetAutoPotValue, (w) => { w.writeU8(p.Stat); w.writeU32(p.Value); }),
  /** Grid = MirGridType.HeroHPItem / HeroMPItem; ItemIndex = ItemInfo index (0 clears). */
  SetAutoPotItem: (p: { Grid: number; ItemIndex: number }) => frame(C.SetAutoPotItem, (w) => { w.writeU8(p.Grid); w.writeI32(Number(p.ItemIndex)); }),
  SetHeroBehaviour: (p: { Behaviour: number }) => frame(C.SetHeroBehaviour, (w) => w.writeU8(p.Behaviour)),
  /** ListIndex = storage index + 1 (HeroManageDialog). */
  ChangeHero: (p: { ListIndex: number }) => frame(C.ChangeHero, (w) => w.writeI32(p.ListIndex)),
  // — Intelligent creatures (IntelligentCreatureDialog, GameScene.NewIntelligentCreature, the CreaturePickup keys) —
  /** The creature with its edits (name, pickup mode, filter), plus at most one of summon / unsummon / release. */
  UpdateIntelligentCreature: (p: { Creature: ClientIntelligentCreature; SummonMe: boolean; UnSummonMe: boolean; ReleaseMe: boolean }) =>
    frame(C.UpdateIntelligentCreature, (w) => { writeCreature(w, p.Creature); w.writeBool(p.SummonMe); w.writeBool(p.UnSummonMe); w.writeBool(p.ReleaseMe); }),
  /** MouseMode true = KeybindOptions.CreaturePickup (pick up at the mouse cell), false = CreatureAutoPickup (semi-auto). */
  IntelligentCreaturePickup: (p: { MouseMode: boolean; Location: { X: number; Y: number } }) =>
    frame(C.IntelligentCreaturePickup, (w) => { w.writeBool(p.MouseMode); w.writeI32(p.Location.X); w.writeI32(p.Location.Y); }),
  RequestIntelligentCreatureUpdates: (p: { Update: boolean }) => frame(C.RequestIntelligentCreatureUpdates, (w) => w.writeBool(p.Update)),
  /** Owner: the guild territory's current owner name (GuildTerritoryDialog). Needs the GetClientPacket case added in Phase 2.1. */
  PurchaseGuildTerritory: (p: { Owner: string }) => frame(C.PurchaseGuildTerritory, (w) => w.writeString(p.Owner)),
  // — Bag / storage / mail batches (Phase 2.1) —
  /** Destroys a bag item (HeroInventory = the hero's bag). */
  DeleteItem: (p: { UniqueID: bigint; Count: number; HeroInventory: boolean }) => frame(C.DeleteItem, (w) => { w.writeU64(p.UniqueID); w.writeU16(p.Count); w.writeBool(p.HeroInventory); }),
  RemoveStoragePassword: (p: { CurrentPassword: string }) => frame(C.RemoveStoragePassword, (w) => w.writeString(p.CurrentPassword)),
  /** One S.SellItem reply per entry; the server caps the list at the bag size and ignores repeated UniqueIDs. */
  SellItems: (p: { Items: { UniqueID: bigint; Count: number }[] }) => frame(C.SellItems, (w) => {
    w.writeI32(p.Items.length);
    for (const i of p.Items) { w.writeU64(BigInt(i.UniqueID)); w.writeU16(i.Count); }
  }),
  /** Special = true: special repair (SRepairItem rules). Bag items only; capped at the bag size, repeats ignored. */
  RepairItems: (p: { Special: boolean; UniqueIDs: bigint[] }) => frame(C.RepairItems, (w) => {
    w.writeBool(p.Special); w.writeI32(p.UniqueIDs.length); for (const id of p.UniqueIDs) w.writeU64(BigInt(id));
  }),
  /** Collects every ready parcel (CollectParcel rules); stops at the first full-bag failure. */
  CollectAllParcels: () => frame(C.CollectAllParcels),
  /** Server caps the list at 100 and ignores repeated IDs. */
  ReadMails: (p: { MailIDs: bigint[] }) => frame(C.ReadMails, (w) => { w.writeI32(p.MailIDs.length); for (const id of p.MailIDs) w.writeU64(BigInt(id)); }),
  /** Grid: MirGridType.Inventory (1, bag cells only, never the belt) or MirGridType.Storage (4). Mode 0 = sort, 1 = stack then compact.
   *  Reply: S.UserSlotsRefresh (inventory) or S.UserStorage (storage); 2 s server cooldown. */
  SortItems: (p: { Grid: number; Mode: number }) => frame(C.SortItems, (w) => { w.writeU8(p.Grid); w.writeU8(p.Mode); }),
  // — Phase 2.4 —
  /** Spell = Spell enum value of a hero spell. Needs a spawned hero that knows it; reply S.HeroAutoCast (full disabled list). */
  HeroSpellToggle: (p: { Spell: number; AutoCast: boolean }) => frame(C.HeroSpellToggle, (w) => { w.writeU8(p.Spell); w.writeBool(p.AutoCast); }),
  /** MapIndex = MapInfo.Index: the current map or one with a BigMap. Needs ServerRules.MapDetailsEnabled; 1 s server cooldown. Reply S.MapDetails. */
  RequestMapDetails: (p: { MapIndex: number }) => frame(C.RequestMapDetails, (w) => w.writeI32(p.MapIndex)),
  // — Phase 2.5 —
  /** Needs a guild; 5 s server cooldown (ignored silently inside it). Reply S.GuildMemberDetails (maybe several parts). */
  RequestGuildMemberDetails: () => frame(C.RequestGuildMemberDetails),
  /** Needs a guild; 5 s server cooldown (ignored silently inside it). Reply S.GuildLog. */
  RequestGuildLog: () => frame(C.RequestGuildLog),
  /** Page is 0-based; the server clamps it. Reply S.GuildRankings. */
  GetGuildRanking: (p: { Page: number }) => frame(C.GetGuildRanking, (w) => w.writeI32(p.Page)),
  /** Level range [MinLevel, MaxLevel] (u16); 3 s server cooldown. Reply S.MentorCandidates (or a chat line if not eligible / cooling down). */
  FindMentor: (p: { MinLevel: number; MaxLevel: number }) => frame(C.FindMentor, (w) => { w.writeU16(p.MinLevel); w.writeU16(p.MaxLevel); }),
  /** Kind = GuildTerritoryActionKinds value; TerritoryIndex as in S.GuildTerritoryPage; Price (u32) is used by ListForSale only.
   *  Same checks as the NPC BuyGT / ExtendGT / GTSale / GTCancelSale / TeleportGT commands; replies are chat lines (Territories.action re-requests its page). */
  GuildTerritoryAction: (p: { Kind: number; TerritoryIndex: number; Price: number }) =>
    frame(C.GuildTerritoryAction, (w) => { w.writeU8(p.Kind); w.writeI32(p.TerritoryIndex); w.writeU32(p.Price); }),
  /** Server strips control chars, trims, caps at 120 chars, ignores it while chat-banned; no word filter; 10 s cooldown. */
  SetProfile: (p: { Text: string }) => frame(C.SetProfile, (w) => w.writeString(p.Text)),
  // — Phase 2.6 —
  /** Gift a gameshop item: PType as GameshopBuy (0 credit, 1 gold); Quantity i32 (server checks 1..99); Recipient = a mutual friend's
   *  character name (not yourself, mailbox not full). Delivered by mail with the buyer as sender; replies are chat lines. */
  GameshopGift: (p: { GIndex: number; Quantity: number; PType: number; Recipient: string }) =>
    frame(C.GameshopGift, (w) => { w.writeI32(p.GIndex); w.writeI32(p.Quantity); w.writeU8(p.PType); w.writeString(p.Recipient); }),
  /** MarketSearch plus Sort (MarketSearchSort), PriceMin / PriceMax (u32, PriceMax 0 = no max) and MinGrade (ItemGrade byte).
   *  Needs the market page open; reply S.NPCMarket / S.NPCMarketPage as for MarketSearch. */
  MarketSearchEx: (p: { Match: string; Type: number; Usermode: boolean; MinShape: number; MaxShape: number; MarketType: number; Sort: number; PriceMin: number; PriceMax: number; MinGrade: number }) =>
    frame(C.MarketSearchEx, (w) => {
      w.writeString(p.Match); w.writeU8(p.Type); w.writeBool(p.Usermode); w.writeI16(p.MinShape); w.writeI16(p.MaxShape); w.writeU8(p.MarketType);
      w.writeU8(p.Sort); w.writeU32(p.PriceMin); w.writeU32(p.PriceMax); w.writeU8(p.MinGrade);
    }),
  /** ItemIndex = ItemInfo.Index (i32). Needs the market page open; 1 s server cooldown. Reply S.MarketPriceHistory.
   *  Accepts a bigint too: tools/codec-test.ts turns every client field named ItemIndex into a bigint (C.BuyItem's is a ulong). */
  MarketPriceHistory: (p: { ItemIndex: number | bigint }) => frame(C.MarketPriceHistory, (w) => w.writeI32(Number(p.ItemIndex))),
  /** 2 s server cooldown. Reply S.SentMail. */
  RequestSentMail: () => frame(C.RequestSentMail),
  /** Hides one mail this player sent from the sent folder (the recipient keeps it). Reply a fresh S.SentMail. */
  HideSentMail: (p: { MailID: bigint }) => frame(C.HideSentMail, (w) => w.writeU64(p.MailID)),
  /** Answers an S.FriendRequest from Name. Accept adds both sides (fresh S.FriendUpdate to each); decline sends the
   *  requester S.FriendRequestResult. The request must still be pending (60 s). */
  FriendRequestReply: (p: { Name: string; Accept: boolean }) =>
    frame(C.FriendRequestReply, (w) => { w.writeString(p.Name); w.writeBool(p.Accept); }),
  /** Explicit setter (not a flip) for Player.AllowTrade. The server echoes S.AllowToggles. */
  ChangeTrade: (p: { AllowTrade: boolean }) => frame(C.ChangeTrade, (w) => w.writeBool(p.AllowTrade)),
  /** Idempotent setter for one social allow toggle; Toggle = AllowToggle (enums.generated.ts). Undefined values are
   *  rejected by the server. Group/Observe run the same server paths as C.SwitchGroup / the observe toggle.
   *  The server echoes S.AllowToggles. */
  SetAllowToggle: (p: { Toggle: number; Value: boolean }) =>
    frame(C.SetAllowToggle, (w) => { w.writeU8(p.Toggle); w.writeBool(p.Value); }),
} as const;

/** GuildTerritoryActionKind (Shared/Enums.cs, byte). Hand-copied: WebRelay/TsEmitter.cs does not emit that enum yet. */
export const GuildTerritoryActionKinds = { Rent: 0, Extend: 1, ListForSale: 2, CancelSale: 3, Teleport: 4 } as const;

/** C.MarketSearchEx.Sort values (byte constants on the C# class, not an enum). */
export const MarketSearchSort = { None: 0, PriceAscending: 1, PriceDescending: 2, Newest: 3, EndingSoonest: 4 } as const;

export type ClientPacketName = keyof typeof ClientPackets;
