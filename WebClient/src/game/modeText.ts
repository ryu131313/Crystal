// GameScene.ChangeAMode / ChangePMode chat texts (ClientTextKeys.AttackMode_* / PetMode_*, English.json), shared by
// game.ts (the S.ChangeAMode / ChangePMode chat line) and both UIs' mode labels. DOM-free, so game.ts needs no UI import.

import { AttackMode, PetMode } from "../protocol/enums.generated.ts";

export const AMODE_TEXT: Record<number, string> = {
  [AttackMode.Peace]: "[Mode: Peaceful]", [AttackMode.Group]: "[Mode: Group]", [AttackMode.Guild]: "[Mode: Guild]",
  [AttackMode.EnemyGuild]: "[Mode: Enemy Guild]", [AttackMode.RedBrown]: "[Mode: Red/Brown]", [AttackMode.All]: "[Mode: Attack All]",
};
export const PMODE_TEXT: Record<number, string> = {
  [PetMode.Both]: "[Pet: Attack and Move]", [PetMode.MoveOnly]: "[Pet: Do Not Attack]", [PetMode.AttackOnly]: "[Pet: Do Not Move]",
  [PetMode.None]: "[Pet: Do Not Attack or Move]", [PetMode.FocusMasterTarget]: "[Pet: Focus Master Target]",
};
