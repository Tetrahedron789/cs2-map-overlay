import { bindValue, trigger } from "cs2/api";

// Must match MapOverlayUISystem.cs.
const group = "mapOverlay";

export const visible$ = bindValue<boolean>(group, "visible", false);
export const opacity$ = bindValue<number>(group, "opacity", 50);
export const onTop$ = bindValue<boolean>(group, "onTop", true);
export const follow$ = bindValue<boolean>(group, "follow", true);
export const zoom$ = bindValue<number>(group, "zoom", 15);
export const effectiveZoom$ = bindValue<number>(group, "effectiveZoom", 15);
export const scale$ = bindValue<number>(group, "scale", 100);
export const source$ = bindValue<number>(group, "source", 0);
export const customUrl$ = bindValue<string>(group, "customUrl", "");
export const anchorX$ = bindValue<number>(group, "anchorX", 0);
export const anchorZ$ = bindValue<number>(group, "anchorZ", 0);
export const areaMetres$ = bindValue<number>(group, "areaMetres", 3000);
export const canGrowArea$ = bindValue<boolean>(group, "canGrowArea", true);
export const canShrinkArea$ = bindValue<boolean>(group, "canShrinkArea", true);
export const wholeMap$ = bindValue<boolean>(group, "wholeMap", false);
export const wholeMapZoom$ = bindValue<number>(group, "wholeMapZoom", 14);
export const dragging$ = bindValue<boolean>(group, "dragging", false);
export const rotation$ = bindValue<number>(group, "rotation", 0);
export const lat$ = bindValue<number>(group, "lat", 0);
export const lon$ = bindValue<number>(group, "lon", 0);
export const attribution$ = bindValue<string>(group, "attribution", "");
export const tilesReady$ = bindValue<number>(group, "tilesReady", 0);
export const tilesTotal$ = bindValue<number>(group, "tilesTotal", 0);
export const tilesFailed$ = bindValue<number>(group, "tilesFailed", 0);
export const problem$ = bindValue<string>(group, "problem", "");
export const searchStatus$ = bindValue<string>(group, "searchStatus", "");
export const searchResults$ = bindValue<string>(group, "searchResults", "[]");

export const setVisible = (v: boolean) => trigger(group, "setVisible", v);
export const setOpacity = (v: number) => trigger(group, "setOpacity", v);
export const setOnTop = (v: boolean) => trigger(group, "setOnTop", v);
export const setFollow = (v: boolean) => trigger(group, "setFollow", v);
export const setZoom = (v: number) => trigger(group, "setZoom", v);
export const stepArea = (d: number) => trigger(group, "stepArea", d);
export const setWholeMap = (v: boolean) => trigger(group, "setWholeMap", v);
export const setRotation = (v: number) => trigger(group, "setRotation", v);
export const setScale = (v: number) => trigger(group, "setScale", v);
export const setSource = (v: number) => trigger(group, "setSource", v);
export const setCustomUrl = (v: string) => trigger(group, "setCustomUrl", v);
export const alignCentre = () => trigger(group, "alignCentre");
export const setDragging = (v: boolean) => trigger(group, "setDragging", v);
export const nudge = (right: number, forward: number) => trigger(group, "nudge", right, forward);
export const bringHere = () => trigger(group, "bringHere");
export const search = (query: string) => trigger(group, "search", query);
export const suggest = (query: string) => trigger(group, "suggest", query);
export const clearSearch = () => trigger(group, "clearSearch");
export const goTo = (lat: number, lon: number) => trigger(group, "goTo", lat, lon);

// Must match Setting.cs.
export const minScale = 25;
export const maxScale = 200;
export const worldMapKm = 57.344;
export const playableKm = 14.336;

export interface SearchResult {
  name: string;
  lat: number;
  lon: number;
}

// Panel visibility is UI-only state shared by the toolbar button and the panel.
type Listener = () => void;
let panelOpen = false;
const listeners = new Set<Listener>();

export const panelStore = {
  get: () => panelOpen,
  set: (open: boolean) => {
    panelOpen = open;
    // The drag tool is driven from the panel, so it ends with it.
    if (!open) setDragging(false);
    listeners.forEach((l) => l());
  },
  subscribe: (listener: Listener) => {
    listeners.add(listener);
    return () => {
      listeners.delete(listener);
    };
  },
};
