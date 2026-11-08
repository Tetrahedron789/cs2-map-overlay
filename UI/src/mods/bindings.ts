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

// Open tab, kept while the panel is closed.
export type PanelTab = "overlay" | "terrain";
let panelTab: PanelTab = "overlay";
const tabListeners = new Set<Listener>();

export const tabStore = {
  get: () => panelTab,
  set: (tab: PanelTab) => {
    panelTab = tab;
    tabListeners.forEach((l) => l());
  },
  subscribe: (listener: Listener) => {
    tabListeners.add(listener);
    return () => {
      tabListeners.delete(listener);
    };
  },
};

// ---- Projects -------------------------------------------------------------------------

export interface ProjectRow {
  id: string;
  name: string;
  lat: number;
  lon: number;
  locked: boolean;
  terrain: string; // date of the last generated terrain, or ""
}

export const projects$ = bindValue<string>(group, "projects", "[]");
export const activeProject$ = bindValue<string>(group, "activeProject", "");
export const activeProjectName$ = bindValue<string>(group, "activeProjectName", "");
export const activeProjectTerrain$ = bindValue<string>(group, "activeProjectTerrain", "");
export const projectLocked$ = bindValue<boolean>(group, "projectLocked", false);
export const projectDirty$ = bindValue<boolean>(group, "projectDirty", false);
export const createProject = (name: string) => trigger(group, "createProject", name);
export const openProject = (id: string) => trigger(group, "openProject", id);
export const saveProject = () => trigger(group, "saveProject");
export const closeProject = () => trigger(group, "closeProject");
export const deleteProject = (id: string) => trigger(group, "deleteProject", id);
export const setProjectLocked = (locked: boolean) => trigger(group, "setProjectLocked", locked);

// ---- Terrain (editor only) ------------------------------------------------------------

export enum TerrainState {
  Idle = 0,
  Downloading = 1,
  Building = 2,
  Done = 3,
  Failed = 4,
  Planting = 5,
}

export const inEditor$ = bindValue<boolean>(group, "inEditor", false);
export const terrainState$ = bindValue<number>(group, "terrainState", 0);
export const terrainProgress$ = bindValue<number>(group, "terrainProgress", 0);
export const terrainProblem$ = bindValue<string>(group, "terrainProblem", "");
export const terrainLowest$ = bindValue<number>(group, "terrainLowest", 0);
export const terrainHighest$ = bindValue<number>(group, "terrainHighest", 0);
export const terrainWaterLevel$ = bindValue<number>(group, "terrainWaterLevel", 0);
export const terrainLowestInGame$ = bindValue<number>(group, "terrainLowestInGame", 0);
export const terrainHeightScale$ = bindValue<number>(group, "terrainHeightScale", 0);
export const terrainLakes$ = bindValue<number>(group, "terrainLakes", 0);
export const terrainSources$ = bindValue<number>(group, "terrainSources", 0);
export const terrainRivers$ = bindValue<number>(group, "terrainRivers", 0);
export const terrainTrees$ = bindValue<number>(group, "terrainTrees", 0);
// options: bit 0 lakes, bit 1 rivers, bit 2 trees, bits 4-5 tree density.
export const buildTerrain = (options: number) => trigger(group, "buildTerrain", options);
export const cancelTerrain = () => trigger(group, "cancelTerrain");
