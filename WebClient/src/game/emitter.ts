// A tiny typed event: the DOM-free models in src/game/ announce changes and prompts through these, and each UI
// (classic or modern) subscribes. `on` returns the unsubscribe function.

export class Emitter<T> {
  private fns = new Set<(v: T) => void>();

  on(fn: (v: T) => void): () => void {
    this.fns.add(fn);
    return () => { this.fns.delete(fn); };
  }

  emit(v: T): void {
    for (const fn of [...this.fns]) fn(v);
  }
}
