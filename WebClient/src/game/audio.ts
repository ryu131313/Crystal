// Sound: port of Client/MirSounds/SoundManager.cs + SoundList.cs on Web Audio. Files come from the client folder's
// Sound\ (WebRelay /sound/, read-only). Index -> file name: Sound\SoundList.lst first, else "NNN-N" (index / 10, index % 10)
// or "MNN-N" for spells (index > 20000); a name without an extension tries .wav then .mp3 (CachedSound).
// One-shots are decoded once and cached; loops (PlaySound loop) and music (PlayMusic) can be stopped.
// Browsers start audio only after a user gesture: the context resumes on the first key/mouse press.
// Volume (0-100, Settings.Volume / MusicVolume) is kept in localStorage.
// Web-only: procedural rain (setRain) for the atmosphere's shader rain, which has no sound file on the desktop.

import { gameSignal } from "./scope.ts";
import { assetUrl, fetchAsset } from "../assets/manifest.ts";

const EXTENSIONS = [".wav", ".mp3"]; // SoundManager.SupportedFileTypes

/** The procedural rain's overall level (every band and the close drops go through `out`); 0.3 = about -10 dB, the user found 1 far too loud (2026-10-14). */
const RAIN_VOLUME = 0.3;

/** The procedural rain's graph: looped stereo noise -> hiss (high band) / patter (mid band) / rumble (low band) -> out -> master. */
interface RainVoice {
  src: AudioBufferSourceNode;
  noise: AudioBuffer;
  out: GainNode;
  body: GainNode;
  low: GainNode;
  level: number;
  setAt: number;
  tickAt: number;
  /** Fading out: stop the source at stopAt (context seconds). */
  stopAt: number;
}

export class SoundManager {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private musicGain: GainNode | null = null;
  private readonly indexes = new Map<number, string>();
  private readonly buffers = new Map<string, Promise<AudioBuffer | null>>();
  private readonly loops = new Map<number, AudioBufferSourceNode>();
  private music: AudioBufferSourceNode | null = null;
  private rain: RainVoice | null = null;
  private splashNoise: AudioBuffer | null = null;
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

  /** Web-only procedural rain (atmosphere.ts): `level` 0..1, called every frame; the bands ramp smoothly to it, and close drops tick at random,
   *  panned across the stereo field, more often the harder it rains. 0 fades it out and then stops it. Waits for the context to be unlocked. */
  setRain(level: number): void {
    const ctx = this.ctx;
    if (!ctx || ctx.state !== "running" || !this.master) return;
    level = Math.max(0, Math.min(1, level));
    const t = ctx.currentTime;
    let r = this.rain;
    if (level <= 0) {
      if (!r) return;
      if (r.stopAt === Infinity) {
        r.out.gain.setTargetAtTime(0, t, 0.7);
        r.stopAt = t + 4;
      } else if (t >= r.stopAt) {
        this.stopRain();
      }
      return;
    }
    if (!r) r = this.rain = SoundManager.buildRain(ctx, this.master);
    if (r.stopAt !== Infinity || Math.abs(level - r.level) > 0.01 || t - r.setAt > 0.5) {
      r.stopAt = Infinity;
      r.level = level;
      r.setAt = t;
      r.out.gain.setTargetAtTime((0.08 + 0.27 * level) * RAIN_VOLUME, t, 0.8);
      r.body.gain.setTargetAtTime(0.2 + 0.8 * level, t, 0.8);
      r.low.gain.setTargetAtTime(0.7 * level * level, t, 1.2);
    }
    const dt = Math.min(0.1, Math.max(0, t - r.tickAt));
    r.tickAt = t;
    if (this._volume <= 0) return;
    for (let n = dt * (4 + 22 * level); n > 0; n--) if (Math.random() < n) SoundManager.rainTick(ctx, r, level);
  }

  /** Web-only footfall splash (atmosphere.ts, the user's own steps in puddles): a low slap, then the higher spray falling back; `level` 0..1. */
  splash(level: number): void {
    const ctx = this.ctx;
    if (!ctx || ctx.state !== "running" || !this.master || this._volume <= 0 || level <= 0) return;
    const noise = (this.splashNoise ??= SoundManager.noiseBuffer(ctx, 1));
    const t = ctx.currentTime;
    for (let i = 0; i < 2; i++) {
      const at = t + i * 0.05, len = i === 0 ? 0.12 : 0.2;
      const src = ctx.createBufferSource();
      src.buffer = noise;
      const f = ctx.createBiquadFilter();
      f.type = "bandpass";
      f.frequency.value = i === 0 ? 600 + Math.random() * 400 : 2400 + Math.random() * 1600;
      f.Q.value = i === 0 ? 0.9 : 1.4;
      const g = ctx.createGain();
      g.gain.setValueAtTime(0.0001, at);
      g.gain.exponentialRampToValueAtTime((i === 0 ? 0.45 : 0.2) * level, at + 0.008);
      g.gain.exponentialRampToValueAtTime(0.0001, at + len);
      src.connect(f).connect(g).connect(this.master);
      src.start(at, Math.random() * 0.6, len + 0.02);
    }
  }

  /** `seconds` of stereo white noise. */
  private static noiseBuffer(ctx: AudioContext, seconds: number): AudioBuffer {
    const noise = ctx.createBuffer(2, Math.round(ctx.sampleRate * seconds), ctx.sampleRate);
    for (let ch = 0; ch < 2; ch++) {
      const data = noise.getChannelData(ch);
      for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    }
    return noise;
  }

  private static buildRain(ctx: AudioContext, master: GainNode): RainVoice {
    const noise = SoundManager.noiseBuffer(ctx, 3);
    const src = ctx.createBufferSource();
    src.buffer = noise;
    src.loop = true;
    const out = ctx.createGain();
    out.gain.value = 0;
    out.connect(master);
    const band = (type: BiquadFilterType, frequency: number, q: number, gain: number): GainNode => {
      const f = ctx.createBiquadFilter();
      f.type = type;
      f.frequency.value = frequency;
      f.Q.value = q;
      const g = ctx.createGain();
      g.gain.value = gain;
      src.connect(f).connect(g).connect(out);
      return g;
    };
    band("highpass", 3200, 0.7, 0.22); // hiss: distant rain on everything
    const body = band("bandpass", 1100, 0.5, 0); // patter nearby
    const low = band("lowpass", 320, 0.7, 0); // rumble of a downpour
    src.start();
    return { src, noise, out, body, low, level: -1, setAt: 0, tickAt: ctx.currentTime, stopAt: Infinity };
  }

  /** One close drop: a 30-70 ms burst of the noise through a narrow band at a random pitch, panned somewhere left or right. */
  private static rainTick(ctx: AudioContext, r: RainVoice, level: number): void {
    const t = ctx.currentTime + Math.random() * 0.03;
    const src = ctx.createBufferSource();
    src.buffer = r.noise;
    const f = ctx.createBiquadFilter();
    f.type = "bandpass";
    f.frequency.value = 1800 + Math.random() * 4500;
    f.Q.value = 2 + Math.random() * 4;
    const g = ctx.createGain();
    const peak = (0.12 + Math.random() * 0.3) * (0.5 + 0.5 * level);
    const len = 0.03 + Math.random() * 0.04;
    g.gain.setValueAtTime(peak, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + len);
    const pan = ctx.createStereoPanner();
    pan.pan.value = Math.random() * 1.6 - 0.8;
    src.connect(f).connect(g).connect(pan).connect(r.out);
    src.start(t, Math.random() * 2.5, len + 0.01);
  }

  private stopRain(): void {
    const r = this.rain;
    if (!r) return;
    this.rain = null;
    try { r.src.stop(); } catch { /* already stopped */ }
    r.out.disconnect();
  }

  /** Scene end (GameScene.LogOutSuccess stops the looping sounds): every loop and the music, then the context. */
  stopAll(): void {
    this.stopRain();
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
