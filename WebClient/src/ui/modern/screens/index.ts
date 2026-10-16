// The new UI's account screens and loading overlay as one lazily loaded module (web-only, plan phase 4). src/ui/screens.ts
// reaches it only through loadModernScreens()'s dynamic import (and play/main.ts through the same loader), so a Classic
// player never downloads the modern tree (uiLoader.ts; WebRelay/PageBuild.cs modulepreloads main.js's static graph only).

export { createModernLogin, type LoginUi } from "./login.ts";
export { createSelectScene, type SelectUi, type SelectSceneDeps } from "./select.ts";
export { LoadingOverlay } from "./loading.ts";
export { showNotice } from "./notice.ts";
