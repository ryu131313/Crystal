// Sound: port of Client/MirSounds/SoundManager.cs + SoundList.cs on Web Audio. Files come from the client folder's
// Sound\ (WebRelay /sound/, read-only). Index -> file name: Sound\SoundList.lst first, else "NNN-N" (index / 10, index % 10)
// or "MNN-N" for spells (index > 20000); a name without an extension tries .wav then .mp3 (CachedSound).
// One-shots are decoded once and cached; loops (PlaySound loop) and music (PlayMusic) can be stopped.
// Browsers start audio only after a user gesture: the context resumes on the first key/mouse press.
// Volume (0-100, Settings.Volume / MusicVolume) is kept in localStorage.

import { gameSignal } from "./scope.ts";
import { assetUrl, fetchAsset } from "../assets/manifest.ts";

const EXTENSIONS = [".wav", ".mp3"]; // SoundManager.SupportedFileTypes

export class SoundManager {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private musicGain: GainNode | null = null;
  private readonly indexes = new Map<number, string>();
  private readonly buffers = new Map<string, Promise<AudioBuffer | null>>();
  private readonly loops = new Map<number, AudioBufferSourceNode>();
  private music: AudioBufferSourceNode | null = null;
  private musicIndex = -1;
  private listLoaded: Promise<void>;
  private closed = false;
  private _volume = SoundManager.load("crystal.volume", 70);
  private _musicVolume = SoundManager.load("crystal.musicVolume", 40);

  constructor() {
    this.listLoaded = this.loadList();
    const unlock = () => {
      if (this.closed) return;
      this.ensure();
      void this.ctx?.resume();
    };
    // The scene's listeners end with it (stopAll closes the context; a closed one cannot resume)
    window.addEventListener("pointerdown", unlock, { capture: true, signal: gameSignal() });
    window.addEventListener("keydown", unlock, { capture: true, signal: gameSignal() });
  }

  private static load(key: string, fallback: number): number {
    try { const v = Number(localStorage.getItem(key)); return Number.isFinite(v) && localStorage.getItem(key) !== null ? v : fallback; } catch { return fallback; }
  }

  get volume(): number { return this._volume; }
  set volume(v: number) {
    this._volume = Math.max(0, Math.min(100, v));
    if (this.master) this.master.gain.value = this._volume / 100;
    try { localStorage.setItem("crystal.volume", String(this._volume)); } catch { /* storage blocked */ }
  }

  get musicVolume(): number { return this._musicVolume; }
  set musicVolume(v: number) {
    this._musicVolume = Math.max(0, Math.min(100, v));
    if (this.musicGain) this.musicGain.gain.value = this._musicVolume / 100;
    try { localStorage.setItem("crystal.musicVolume", String(this._musicVolume)); } catch { /* storage blocked */ }
  }

  /** SoundList.LoadSoundList: "index: file" lines (spaces removed, ':' or tab separated), first entry wins. */
  private async loadList(): Promise<void> {
    try {
      const url = await assetUrl("sound", "SoundList.lst");
      if (!url) return;
      const res = await fetchAsset(url);
      if (!res.ok) return;
      for (const raw of (await res.text()).split(/\r?\n/)) {
        const split = raw.replace(/ /g, "").split(/[:\t]/);
        if (split.length <= 1) continue;
        if (!/^-?\d+$/.test(split[0]!)) continue; // int.TryParse
        const index = Number.parseInt(split[0]!, 10);
        if (!this.indexes.has(index)) this.indexes.set(index, split[split.length - 1]!);
      }
    } catch { /* no list: computed names only */ }
  }

  private ensure(): AudioContext | null {
    if (this.ctx) return this.ctx;
    try {
      this.ctx = new AudioContext();
      this.master = this.ctx.createGain();
      this.master.gain.value = this._volume / 100;
      this.master.connect(this.ctx.destination);
      this.musicGain = this.ctx.createGain();
      this.musicGain.gain.value = this._musicVolume / 100;
      this.musicGain.connect(this.ctx.destination);
    } catch {
      this.ctx = null;
    }
    return this.ctx;
  }

  /** SoundManager.PlaySound's file name rule. */
  fileName(index: number): string {
    const listed = this.indexes.get(index);
    if (listed) return listed;
    return index > 20000
      ? `M${Math.trunc((index - 20000) / 10)}-${index % 10}`
      : `${String(Math.trunc(index / 10)).padStart(3, "0")}-${index % 10}`;
  }

  private buffer(name: string): Promise<AudioBuffer | null> {
    let p = this.buffers.get(name);
    if (!p) {
      p = (async () => {
        const ctx = this.ensure();
        if (!ctx) return null;
        const hasExt = /\.[a-z0-9]+$/i.test(name);
        const candidates = hasExt ? [name] : EXTENSIONS.map((e) => name + e);
        for (const file of candidates) {
          if (hasExt && !EXTENSIONS.includes(file.slice(file.lastIndexOf(".")).toLowerCase())) return null;
          const url = await assetUrl("sound", file);
          if (!url) continue; // not in the client folder: no request
          const res = await fetchAsset(url);
          if (!res.ok) continue;
          try { return await ctx.decodeAudioData(await res.arrayBuffer()); } catch { return null; }
        }
        return null;
      })();
      this.buffers.set(name, p);
    }
    return p;
  }

  /** SoundManager.PlaySound(index, loop, delay). */
  play(index: number, loop = false, delay = 0): void {
    if (index <= 0) return;
    if (delay > 0) { setTimeout(() => this.play(index, loop), delay); return; }
    if (this._volume <= 0 && !loop) return;
    const ctx = this.ensure();
    if (!ctx || ctx.state !== "running") return; // not unlocked yet: a missed one-shot is better than a late one
    void this.listLoaded.then(() => this.buffer(this.fileName(index))).then((buf) => {
      if (!buf || !this.ctx || !this.master) return;
      const src = this.ctx.createBufferSource();
      src.buffer = buf;
      src.loop = loop;
      src.connect(this.master);
      if (loop) {
        this.loops.get(index)?.stop();
        this.loops.set(index, src);
        src.onended = () => { if (this.loops.get(index) === src) this.loops.delete(index); };
      }
      src.start();
    });
  }

  /** SoundManager.StopSound. */
  stop(index: number): void {
    const src = this.loops.get(index);
    if (!src) return;
    this.loops.delete(index);
    try { src.stop(); } catch { /* already stopped */ }
  }

  /** SoundManager.PlayMusic(index, loop): only SoundList.lst indexes are music (the desktop looks the index up the same way). */
  playMusic(index: number, loop = true): void {
    if (index === this.musicIndex && this.music) return;
    this.stopMusic();
    this.musicIndex = index;
    void this.listLoaded.then(async () => {
      const name = this.indexes.get(index);
      if (!name) return;
      const buf = await this.buffer(name);
      const ctx = this.ctx;
      if (!buf || !ctx || !this.musicGain || this.musicIndex !== index) return;
      const start = () => {
        if (this.musicIndex !== index || this.music) return;
        const src = ctx.createBufferSource();
        src.buffer = buf;
        src.loop = loop;
        src.connect(this.musicGain!);
        src.start();
        this.music = src;
      };
      if (ctx.state === "running") start();
      else ctx.addEventListener("statechange", () => { if (ctx.state === "running") start(); }, { once: true });
    });
  }

  stopMusic(): void {
    this.musicIndex = -1;
    try { this.music?.stop(); } catch { /* already stopped */ }
    this.music = null;
  }

  /** Scene end (GameScene.LogOutSuccess stops the looping sounds): every loop and the music, then the context. */
  stopAll(): void {
    for (const index of [...this.loops.keys()]) this.stop(index);
    this.stopMusic();
    this.closed = true;
    void this.ctx?.close().catch(() => {});
  }
}

/** SoundList constants the web client uses directly (Client/MirSounds/SoundList.cs). */
export const SoundList = {
  LevelUp: 10156, Gold: 10106, EatDrug: 10107, ClickDrug: 10108, Teleport: 10110, ButtonA: 10103, ButtonB: 10104, ButtonC: 10105,
  ClickWeapon: 10111, ClickArmour: 10112, ClickRing: 10113, ClickBracelet: 10114, ClickNecklace: 10115, ClickHelmet: 10116, ClickBoots: 10117, ClickItem: 10118,
  IntroMusic: 10146, SelectMusic: 10147,
} as const;
