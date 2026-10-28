// jsdom has no matchMedia; uPlot (as a bare global) and the sidebar ask for it. A query never matches here.
if (typeof window !== "undefined" && typeof globalThis.matchMedia !== "function") {
  const matchMedia = (query: string) => ({
    matches: false,
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
  });
  Object.assign(globalThis, { matchMedia });
  Object.assign(window, { matchMedia });
}

// Nor ResizeObserver, which the charts use to follow their container's width.
if (typeof window !== "undefined" && typeof globalThis.ResizeObserver !== "function") {
  class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  Object.assign(globalThis, { ResizeObserver });
}
