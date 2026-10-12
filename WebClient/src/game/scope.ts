// The running game scene's lifetime. Window listeners that belong to the scene pass `{ signal: gameSignal() }`, so
// disposing the scene (log out -> character select, GameScene.LogOutSuccess) removes them all at once.

let controller = new AbortController();

/** The current scene's signal (read it when registering a listener). */
export function gameSignal(): AbortSignal {
  return controller.signal;
}

/** Ends the current scene's listeners and starts a fresh scope for the next one. */
export function endGameScope(): void {
  controller.abort();
  controller = new AbortController();
}
