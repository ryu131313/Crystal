// The user's current map, without the DOM: MapControl.Index / FileName / Title / MiniMap / BigMap and the map lights
// (MapControl.Lights, GameScene.Lights, MapControl.MapDarkLight), from S.MapInformation / S.MapChanged, S.TimeOfDay and
// S.MapAtmosphere (LiveDb). The world (game.ts) loads the map file, plays the music and sets the weather; the minimap,
// big map (BigMapDialog: CurrentRecord / MapInformation.BigMap) and NPC dialog (GetNPCInfo's location line) read this.
// handle() never consumes a packet.

import type { ServerPacket } from "../protocol/server.ts";
import { LightSetting } from "../protocol/enums.generated.ts";
import type { LightState } from "./lighting.ts";
import { Emitter } from "./emitter.ts";

export class CurrentMap {
  /** "map" = S.MapInformation / S.MapChanged (a new map); "lights" = only the lights changed (S.TimeOfDay, S.MapAtmosphere). */
  readonly changed = new Emitter<"map" | "lights">();
  /** MapControl.Lights / GameScene.Lights / MapControl.MapDarkLight: one object, changed in place (the renderer keeps it). */
  readonly lights: LightState = { mapLights: LightSetting.Normal, timeOfDay: LightSetting.Day, mapDarkLight: 0 };
  private _index = -1;
  private _fileName = "";
  private _title = "";
  private _miniMapIndex = 0;
  private _bigMapIndex = 0;

  /** MapControl.Index (-1 before the first S.MapInformation). */
  get index(): number { return this._index; }
  /** MapControl.FileName (the .map file the world loads). */
  get fileName(): string { return this._fileName; }
  /** MapControl.Title. */
  get title(): string { return this._title; }
  /** MapControl.MiniMap (MMap library index; 0 = none). */
  get miniMapIndex(): number { return this._miniMapIndex; }
  /** MapInformation.BigMap (MMap library index; BigMapDialog shows nothing while it is 0). */
  get bigMapIndex(): number { return this._bigMapIndex; }

  handle(p: ServerPacket): boolean {
    switch (p.type) {
      case "MapInformation": // GameScene.MapInformation: new MapControl
      case "MapChanged": // GameScene.MapChanged: MapControl.ResetMap / LoadMap
        this._index = p.MapIndex;
        this._fileName = p.FileName;
        this._title = p.Title;
        this._miniMapIndex = p.MiniMap;
        this._bigMapIndex = p.BigMap;
        this.lights.mapLights = p.Lights;
        this.lights.mapDarkLight = p.MapDarkLight;
        this.changed.emit("map");
        break;
      case "TimeOfDay": // GameScene.TimeOfDay: GameScene.Lights
        this.lights.timeOfDay = p.Lights;
        this.changed.emit("lights");
        break;
      case "MapAtmosphere": // GameScene.MapAtmosphere: LiveDb changed the current map's light (applied in place, no reload)
        this.lights.mapLights = p.Lights;
        this.lights.mapDarkLight = p.MapDarkLight;
        this.changed.emit("lights");
        break;
    }
    return false;
  }
}
