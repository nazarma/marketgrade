import { isTauri } from "@tauri-apps/api/core";
import { getCurrentWindow } from "@tauri-apps/api/window";

/** The desktop window full screen and back; in a browser, the page. */
export async function toggleFullScreen(): Promise<void> {
  if (isTauri()) {
    const window = getCurrentWindow();
    await window.setFullscreen(!(await window.isFullscreen()));
    return;
  }
  if (document.fullscreenElement) {
    await document.exitFullscreen();
  } else {
    await document.documentElement.requestFullscreen();
  }
}
