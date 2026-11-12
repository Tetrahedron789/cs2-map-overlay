import { ComponentType, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { FloatingButton, Tooltip } from "cs2/ui";
import {
  Arrow,
  Button,
  Callout,
  Caption,
  Hint,
  LabelRow,
  List,
  Meta,
  ModPanel,
  Progress,
  Section,
  Segmented,
  Slider,
  Stack,
  Stepper,
  Switch,
  Tabs,
  TextField,
  ToggleRow,
  cx,
  useDraft,
} from "kit";
import * as b from "./bindings";
import icon from "images/map-overlay.svg";
import styles from "./panel.module.scss";

const usePanelOpen = () => useSyncExternalStore(b.panelStore.subscribe, b.panelStore.get);

const useText = () => {
  const { translate } = useLocalization();
  return (key: string, fallback: string) => translate("MapOverlay.UI." + key, fallback) ?? fallback;
};

// Appended to "GameTopLeft": the game lays these out in a row next to other mods' buttons.
export const ToolbarButton = () => {
  const open = usePanelOpen();
  const text = useText();
  return (
    <FloatingButton
      src={icon}
      selected={open}
      tooltipLabel={text("Title", "Real World Maps")}
      onSelect={() => b.panelStore.set(!open)}
    />
  );
};

const formatPercent = (v: number) => `${Math.round(v * 10) / 10}%`;
const formatKm = (km: number) => `${km >= 100 ? km.toFixed(1) : km.toFixed(2)} km`;

// ---- Projects -----------------------------------------------------------------------

const useProjectRows = (): b.ProjectRow[] => {
  const json = useValue(b.projects$);
  try {
    return JSON.parse(json);
  } catch {
    return [];
  }
};

// Creating a project is its own step: only the name, and what happens next.
const NewProject = ({ onDone }: { onDone: () => void }) => {
  const text = useText();
  const [name, setName] = useState("");
  const [missing, setMissing] = useState(false);

  const create = () => {
    if (name.trim().length === 0) {
      setMissing(true);
      return;
    }
    b.tabStore.set("overlay");
    b.createProject(name.trim());
    onDone();
  };

  return (
    <>
      <Stack>
        <div className={styles.viewTitle}>{text("ProjectNew", "New project")}</div>
        <Hint>{text("ProjectNewHint", "A project holds one city: where its map sits and how it is shown.")}</Hint>
      </Stack>
      <Stack>
        <Caption>{text("ProjectName", "Project name")}</Caption>
        <TextField
          value={name}
          placeholder={text("ProjectNewPlaceholder", "For example, Tokyo")}
          action={text("ProjectCreate", "Create")}
          onChange={(v) => {
            setName(v);
            setMissing(false);
          }}
          onSubmit={create}
        />
        {missing ? (
          <Hint warning>{text("ProjectNameMissing", "Enter a name for the project.")}</Hint>
        ) : (
          <Hint>{text("ProjectNameHint", "You will search for the place in the next step.")}</Hint>
        )}
      </Stack>
      <Stack>
        <Button kind="ghost" wide onClick={onDone}>
          {text("Cancel", "Cancel")}
        </Button>
      </Stack>
    </>
  );
};

const ProjectRow = ({ row, busy }: { row: b.ProjectRow; busy: boolean }) => {
  const text = useText();
  const [deleting, setDeleting] = useState(false);
  if (deleting) {
    return (
      <Callout
        actions={
          <>
            <Button onClick={() => setDeleting(false)}>{text("Cancel", "Cancel")}</Button>
            <Button kind="primary" onClick={() => b.deleteProject(row.id)}>
              {text("ProjectDelete", "Delete")}
            </Button>
          </>
        }
      >
        {text("ProjectDeleteConfirm", "Delete \"{name}\"? This cannot be undone. Maps you already saved are not affected.").replace("{name}", row.name)}
      </Callout>
    );
  }
  return (
    <div className={styles.projectRow}>
      <div className={styles.projectInfo}>
        <div className={styles.projectName}>{row.name}</div>
        <Hint>
          {`${row.lat.toFixed(3)}, ${row.lon.toFixed(3)}`}
          {row.locked ? ` · ${text("ProjectLockedTag", "locked")}` : ""}
          {row.terrain ? ` · ${text("ProjectTerrainTag", "terrain {date}").replace("{date}", row.terrain)}` : ""}
        </Hint>
      </div>
      {!busy && (
        <div className={styles.projectRowButtons}>
          <Button
            kind="primary"
            onClick={() => {
              b.tabStore.set("overlay");
              b.openProject(row.id);
            }}
          >
            {text("ProjectOpen", "Load")}
          </Button>
          <Button kind="ghost" onClick={() => setDeleting(true)}>
            {text("ProjectDelete", "Delete")}
          </Button>
        </div>
      )}
    </div>
  );
};

// What the panel opens to: the projects, or a clear note that there are none yet.
const ProjectHome = ({ busy }: { busy: boolean }) => {
  const text = useText();
  const rows = useProjectRows();
  const [creating, setCreating] = useState(false);

  if (creating) return <NewProject onDone={() => setCreating(false)} />;

  return (
    <>
      <Stack>
        <div className={styles.viewTitle}>{text("ProjectsTitle", "Your projects")}</div>
      </Stack>
      {rows.length === 0 ? (
        <>
          <div className={styles.emptyState}>
            <div className={styles.emptyTitle}>{text("ProjectsEmpty", "No projects yet")}</div>
            <Hint>{text("ProjectsEmptyHint", "Create one for each city you want to work on.")}</Hint>
          </div>
          <Stack>
            <Caption>{text("AboutTitle", "What you can do")}</Caption>
            <LabelRow label={text("TabOverlay", "Overlay")} value="" />
            <Hint>{text("AboutOverlay", "Shows a real map over the ground, in the Map Editor and in your city.")}</Hint>
          </Stack>
          <Stack>
            <LabelRow label={text("TabTerrain", "Terrain")} value="" />
            <Hint>{text("AboutTerrain", "Builds the map from real elevation, water and trees. Map Editor only.")}</Hint>
          </Stack>
        </>
      ) : (
        <Stack>
          {rows.map((row) => (
            <ProjectRow key={row.id} row={row} busy={busy} />
          ))}
        </Stack>
      )}
      <Stack>
        <Button kind="primary" wide onClick={() => setCreating(true)}>
          {text("ProjectNew", "New project")}
        </Button>
      </Stack>
    </>
  );
};

const ProjectBar = ({ busy }: { busy: boolean }) => {
  const text = useText();
  const name = useValue(b.activeProjectName$);
  const locked = useValue(b.projectLocked$);
  const dirty = useValue(b.projectDirty$);
  const active = useValue(b.activeProject$);
  const [deleting, setDeleting] = useState(false);

  return (
    <div className={styles.projectBar}>
      <div className={styles.projectTitle}>
        <Caption>{text("Project", "Project")}</Caption>
        <div className={styles.projectName}>{name}</div>
      </div>
      <div className={styles.projectButtons}>
        <Button kind={dirty ? "primary" : "default"} onClick={b.saveProject}>
          {dirty ? text("ProjectSave", "Save") : text("ProjectSavedState", "Saved")}
        </Button>
        <Button kind={locked ? "primary" : "default"} onClick={() => b.setProjectLocked(!locked)}>
          {locked ? text("ProjectUnlock", "Unlock") : text("ProjectLock", "Lock")}
        </Button>
        {!busy && (
          <Button kind="ghost" onClick={b.closeProject}>
            {text("ProjectClose", "Close")}
          </Button>
        )}
        {!busy && (
          <Button kind="ghost" onClick={() => setDeleting(true)}>
            {text("ProjectDelete", "Delete")}
          </Button>
        )}
      </div>
      {deleting && (
        <Callout
          actions={
            <>
              <Button onClick={() => setDeleting(false)}>{text("Cancel", "Cancel")}</Button>
              <Button
                kind="primary"
                onClick={() => {
                  setDeleting(false);
                  b.deleteProject(active);
                }}
              >
                {text("ProjectDelete", "Delete")}
              </Button>
            </>
          }
        >
          {text("ProjectDeleteConfirm", "Delete \"{name}\"? This cannot be undone. Maps you already saved are not affected.").replace("{name}", name)}
        </Callout>
      )}
      <Hint>
        {locked
          ? text("ProjectLockedHint", "The map is locked: search, moving, rotation and scale are off so it cannot shift.")
          : text("ProjectUnlockedHint", "Once the map sits where you want it, lock it to avoid shifting it by accident.")}
      </Hint>
    </div>
  );
};

// ---- Search -------------------------------------------------------------------------

const SearchBox = () => {
  const text = useText();
  const status = useValue(b.searchStatus$);
  const resultsJson = useValue(b.searchResults$);
  const [query, setQuery] = useState("");
  const timer = useRef<number | undefined>(undefined);
  let results: b.SearchResult[] = [];
  try {
    results = JSON.parse(resultsJson);
  } catch {
    results = [];
  }

  useEffect(() => () => window.clearTimeout(timer.current), []);

  // Suggestions follow typing after a short pause, so each keystroke is not a request.
  const type = (value: string) => {
    setQuery(value);
    window.clearTimeout(timer.current);
    const trimmed = value.trim();
    if (trimmed.length < 3) {
      b.clearSearch();
      return;
    }
    timer.current = window.setTimeout(() => b.suggest(trimmed), 350);
  };

  const submit = () => {
    window.clearTimeout(timer.current);
    if (query.trim().length > 0) b.search(query);
  };

  return (
    <Stack>
      <TextField
        value={query}
        placeholder={text("SearchPlaceholder", "Place, address or coordinates")}
        action={text("Search", "Search")}
        onChange={type}
        onSubmit={submit}
      />
      {status === "" && query.length === 0 && <Hint>{text("SearchFirstHint", "Search for this project's city to bring its map here.")}</Hint>}
      {status === "searching" && <Hint>{text("Searching", "Searching…")}</Hint>}
      {status === "empty" && <Hint>{text("NoResults", "No results.")}</Hint>}
      {status === "error" && <Hint warning>{text("SearchError", "Search failed.")}</Hint>}
      {results.length > 0 && (
        <>
          <List
            items={results}
            label={(r) => r.name}
            onPick={(r) => {
              window.clearTimeout(timer.current);
              setQuery(r.name);
              b.goTo(r.lat, r.lon);
              b.clearSearch();
            }}
          />
          <Hint>{text("SearchHint", "The chosen place appears where the camera looks.")}</Hint>
        </>
      )}
    </Stack>
  );
};

// ---- Position -----------------------------------------------------------------------

const steps = [1, 5, 25, 100, 1000];
const rotations = [-5, -0.5, 0, 0.5, 5];

const Position = () => {
  const text = useText();
  const [step, setStep] = useState(5);
  const rotation = useValue(b.rotation$);
  const dragging = useValue(b.dragging$);
  const rotate = (delta: number) => b.setRotation(delta === 0 ? 0 : Math.round((rotation + delta) * 10) / 10);

  return (
    <Section id="position" title={text("Move", "Position")} summary={`${rotation.toFixed(1)}°`} defaultOpen>
      <Stack>
        <Button kind={dragging ? "primary" : "ghost"} wide onClick={() => b.setDragging(!dragging)}>
          {dragging ? text("DragActive", "Dragging: click to finish") : text("Drag", "Drag on the map")}
        </Button>
        {dragging && (
          <Hint>
            {text(
              "DragHint",
              "Drag to move the map. Shift + drag turns it around the point the camera looks at. Right click or Esc to finish."
            )}
          </Hint>
        )}
      </Stack>
      <div className={cx(styles.moveRow)}>
        {/* Rows of flex boxes: the game's UI engine has no CSS grid. */}
        <div className={styles.pad}>
          <div className={styles.padRow}>
            <button className={styles.padButton} onClick={() => b.nudge(0, step)}>
              <Arrow direction="up" />
            </button>
          </div>
          <div className={styles.padRow}>
            <button className={styles.padButton} onClick={() => b.nudge(-step, 0)}>
              <Arrow direction="left" />
            </button>
            <Tooltip tooltip={text("BringHere", "Bring centre here")}>
              <button className={cx(styles.padButton, styles.padCenter)} onClick={() => b.bringHere()}>
                <span className={styles.target}>
                  <span className={styles.targetDot} />
                </span>
              </button>
            </Tooltip>
            <button className={styles.padButton} onClick={() => b.nudge(step, 0)}>
              <Arrow direction="right" />
            </button>
          </div>
          <div className={styles.padRow}>
            <button className={styles.padButton} onClick={() => b.nudge(0, -step)}>
              <Arrow direction="down" />
            </button>
          </div>
        </div>
        <div className={styles.moveSide}>
          <Caption>{text("Step", "Step")}</Caption>
          <Segmented options={steps} value={step} format={(s) => (s >= 1000 ? `${s / 1000} km` : `${s}`)} onSelect={setStep} />
          <Hint>{text("MoveHint", "Arrows follow the camera view. ◎ brings the map centre to the camera.")}</Hint>
        </div>
      </div>
      <LabelRow label={text("Rotation", "Rotation")} value={`${rotation.toFixed(1)}°`} />
      <Segmented
        options={rotations}
        format={(r) => (r === 0 ? "0°" : `${r > 0 ? "+" : "−"}${Math.abs(r)}°`)}
        onSelect={rotate}
      />
    </Section>
  );
};

// ---- Scale --------------------------------------------------------------------------

const scalePresets = [50, 75, 100, 150, 200];

const Scale = () => {
  const text = useText();
  const scale = useValue(b.scale$);
  const anchorX = useValue(b.anchorX$);
  const anchorZ = useValue(b.anchorZ$);
  // Terraining sizes the whole world map: at 100% that is 57.344 km of real ground.
  const terrainingKm = (b.worldMapKm * 100) / scale;
  const [draft, setDraft] = useDraft(terrainingKm.toFixed(3));
  const [invalid, setInvalid] = useState(false);
  const centred = Math.abs(anchorX) < 0.5 && Math.abs(anchorZ) < 0.5;
  const clamp = (v: number) => Math.max(b.minScale, Math.min(b.maxScale, v));

  const applyTerraining = (value: string) => {
    const km = parseFloat(value.replace(",", "."));
    const percent = (b.worldMapKm * 100) / km;
    if (!isFinite(percent) || percent < b.minScale - 0.01 || percent > b.maxScale + 0.01) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    b.setScale(clamp(percent));
  };

  return (
    <Section id="scale" title={text("Scale", "Scale")} summary={scale === 100 ? "1:1" : formatPercent(scale)}>
      <Slider
        label={text("ScaleLabel", "Map scale")}
        value={scale}
        min={b.minScale}
        max={b.maxScale}
        step={0.5}
        format={formatPercent}
        onCommit={(v) => b.setScale(clamp(v))}
      />
      <Stack>
        <Segmented options={scalePresets} value={scale} format={(v) => (v === 100 ? "1:1" : `${v}%`)} onSelect={b.setScale} />
        <Hint>
          {text("ScaleHint", "The playable area holds {km} of real ground.").replace("{km}", formatKm((b.playableKm * 100) / scale))}
        </Hint>
      </Stack>
      <Stack>
        <Caption>{text("TerrainingSize", "Terraining map size (km)")}</Caption>
        <TextField value={draft} action={text("Apply", "Apply")} onChange={setDraft} onSubmit={applyTerraining} />
        {invalid ? (
          <Hint warning>
            {text("TerrainingRange", "Enter a size between {min} and {max} km.")
              .replace("{min}", ((b.worldMapKm * 100) / b.maxScale).toFixed(3))
              .replace("{max}", ((b.worldMapKm * 100) / b.minScale).toFixed(3))}
          </Hint>
        ) : (
          <Hint>{text("TerrainingHint", "Same value as \"Map size\" in Terraining. 57.344 km is 1:1.")}</Hint>
        )}
      </Stack>
      <Stack last>
        <Button kind={centred ? "default" : "ghost"} wide onClick={b.alignCentre}>
          {centred ? text("Centred", "Map centre is on the city centre") : text("AlignCentre", "Put map centre on the city centre")}
        </Button>
        <Hint>
          {text(
            "AlignHint",
            "Heightmaps are centred on the city. Search the same centre point you used in Terraining, then press this. Rotation must match too."
          )}
        </Hint>
      </Stack>
    </Section>
  );
};

// ---- Map source and loading ---------------------------------------------------------

const sources = [0, 1, 2];

const MapSource = () => {
  const text = useText();
  const source = useValue(b.source$);
  const customUrl = useValue(b.customUrl$);
  const [draft, setDraft] = useDraft(customUrl);
  const names = [text("SourceOsm", "OpenStreetMap"), text("SourceTopo", "Topographic"), text("SourceCustom", "Custom")];

  return (
    <Stack>
      <Caption>{text("Source", "Map style")}</Caption>
      <Segmented options={sources} value={source} format={(i) => names[i]} onSelect={b.setSource} />
      {source === 1 && <Hint>{text("TopoHint", "Contour lines and hill shading: useful to line up a heightmap.")}</Hint>}
      {source === 2 && (
        <div className={styles.customUrl}>
          <TextField
            value={draft}
            placeholder="https://…/{z}/{x}/{y}.png"
            action={text("Apply", "Apply")}
            onChange={setDraft}
            onSubmit={(v) => b.setCustomUrl(v)}
          />
          <Hint>
            {text(
              "CustomHint",
              "Tile URL with {z}, {x} and {y}. Paste one from a provider where you have your own key (MapTiler, Stadia, Thunderforest…) and follow its terms."
            )}
          </Hint>
        </div>
      )}
    </Stack>
  );
};

const Loading = () => {
  const text = useText();
  const zoom = useValue(b.zoom$);
  const effectiveZoom = useValue(b.effectiveZoom$);
  const areaMetres = useValue(b.areaMetres$);
  const canGrowArea = useValue(b.canGrowArea$);
  const canShrinkArea = useValue(b.canShrinkArea$);
  const wholeMap = useValue(b.wholeMap$);
  const wholeMapZoom = useValue(b.wholeMapZoom$);
  const onTop = useValue(b.onTop$);
  const follow = useValue(b.follow$);
  const source = useValue(b.source$);
  const [confirming, setConfirming] = useState(false);
  const sourceNames = ["OSM", "Topo", text("SourceCustom", "Custom")];

  // "Whole map" is the step after the largest area. It asks first when it lowers the detail.
  const changeArea = (d: number) => {
    setConfirming(false);
    if (wholeMap) {
      if (d < 0) b.setWholeMap(false);
    } else if (d > 0 && !canGrowArea) {
      if (zoom > wholeMapZoom) setConfirming(true);
      else b.setWholeMap(true);
    } else if (d > 0 || canShrinkArea) b.stepArea(d);
  };

  return (
    <Section id="map" title={text("Loading", "Map")} summary={`${sourceNames[source] ?? ""} · ${effectiveZoom}`}>
      <MapSource />
      <Stepper label={text("Detail", "Detail")} value={`${zoom}`} onChange={(d) => b.setZoom(zoom + d)} />
      {effectiveZoom < zoom && (
        <div className={styles.indent}>
          <Hint>{text("DetailLimited", "Using {z}: the area needs too many tiles for more.").replace("{z}", `${effectiveZoom}`)}</Hint>
        </div>
      )}
      <Stepper label={text("Area", "Area")} value={wholeMap ? text("WholeMap", "Whole map") : `${(areaMetres / 1000).toFixed(1)} km`} onChange={changeArea} />
      {confirming && (
        <Callout
          actions={
            <>
              <Button onClick={() => setConfirming(false)}>{text("Cancel", "Cancel")}</Button>
              <Button
                kind="primary"
                onClick={() => {
                  setConfirming(false);
                  b.setWholeMap(true);
                }}
              >
                {text("Continue", "Continue")}
              </Button>
            </>
          }
        >
          {text("WholeMapConfirm", "To load the whole map, detail drops from {from} to {to}.")
            .replace("{from}", `${zoom}`)
            .replace("{to}", `${wholeMapZoom}`)}
        </Callout>
      )}
      {wholeMap && (
        <Stack>
          <Hint>{text(
              "WholeMapHint",
              "Covers the whole playable area. To go easy on the tile server, new tiles download only around where the camera looks; move the camera to fill in the rest."
            )}</Hint>
        </Stack>
      )}
      <ToggleRow label={text("OnTop", "On top of everything")} value={onTop} onChange={b.setOnTop} />
      {!wholeMap && <ToggleRow label={text("Follow", "Load where you look")} value={follow} onChange={b.setFollow} />}
    </Section>
  );
};

// ---- Terrain (editor only) ---------------------------------------------------------

const Terrain = () => {
  const text = useText();
  const state = useValue(b.terrainState$);
  const progress = useValue(b.terrainProgress$);
  const problem = useValue(b.terrainProblem$);
  const lowest = useValue(b.terrainLowest$);
  const highest = useValue(b.terrainHighest$);
  const waterLevel = useValue(b.terrainWaterLevel$);
  const lowestInGame = useValue(b.terrainLowestInGame$);
  const heightScale = useValue(b.terrainHeightScale$);
  const lakes = useValue(b.terrainLakes$);
  const sources = useValue(b.terrainSources$);
  const [confirming, setConfirming] = useState(false);
  const rivers = useValue(b.terrainRivers$);
  const trees = useValue(b.terrainTrees$);
  const [water, setWater] = useState(true);
  const [withRivers, setWithRivers] = useState(true);
  const [withTrees, setWithTrees] = useState(true);
  const [density, setDensity] = useState(1);
  const [smoothing, setSmoothing] = useState(1);
  const densityNames = [text("DensityLow", "Low"), text("DensityMedium", "Medium"), text("DensityHigh", "High")];
  const busy = state === b.TerrainState.Downloading || state === b.TerrainState.Building || state === b.TerrainState.Planting;
  const problems: Record<string, [string, string]> = {
    Network: ["TerrainNetwork", "Elevation data did not download. Check the connection and try again."],
    Build: ["TerrainBuild", "The terrain could not be built. See MapOverlay.log."],
    NotEditor: ["TerrainNotEditor", "Terrain can only be generated in the map editor."],
    Cover: ["TerrainCover", "Land cover did not download. Try again, or turn off Trees."],
    Water: ["TerrainWaterFailed", "Water data did not download, so this terrain has no water and trees may stand where it should be. Generate again to retry."],
    NoTrees: ["TerrainNoTrees", "No tree species were found."],
    LegacyWater: ["TerrainLegacyWater", "This map uses the old water sources: lake beds were carved but not filled."],
  };
  const message = problems[problem];
  const metres = (v: number) => `${Math.round(v)} m`;
  const generatedOn = useValue(b.activeProjectTerrain$);
  const locked = useValue(b.projectLocked$);

  return (
    <>
      <Stack>
        <Hint>
          {text(
            "TerrainHint",
            "Builds the playable area and the world map from real elevation, framed exactly like the Overlay map: same centre, rotation and scale. The land is fitted to the current sea level (Water panel): set it first if you want another one."
          )}
        </Hint>
      </Stack>
      {state === b.TerrainState.Idle && generatedOn && (
        <Hint>
          {text("TerrainGeneratedBefore", "This project generated terrain on {date}. If this map is not that one, it does not have it: open the saved map or generate again.").replace("{date}", generatedOn)}
        </Hint>
      )}
      {state === b.TerrainState.Done && (
        <Hint>{text("TerrainPausedHint", "Generated while paused, the water can look wrong on dry ground until you press play.")}</Hint>
      )}
      {state === b.TerrainState.Done && !locked && <Hint>{text("TerrainLockHint", "Terrain and map now match. Lock the project to keep them that way.")}</Hint>}
      {confirming ? (
        <Callout
          actions={
            <>
              <Button onClick={() => setConfirming(false)}>{text("Cancel", "Cancel")}</Button>
              <Button
                kind="primary"
                onClick={() => {
                  setConfirming(false);
                  b.buildTerrain((water ? 1 : 0) | (withRivers ? 2 : 0) | (withTrees ? 4 : 0) | (density << 4) | (smoothing << 6));
                }}
              >
                {text("TerrainReplace", "Replace terrain")}
              </Button>
            </>
          }
        >
          {withTrees
            ? text("TerrainConfirmAll", "This replaces the terrain of this map, including the world map, and removes its trees and, with Lakes and sea or Rivers on, its water sources. It cannot be undone.")
            : water || withRivers
            ? text("TerrainConfirmWater", "This replaces the terrain of this map, including the world map, and all its water sources. It cannot be undone.")
            : text("TerrainConfirm", "This replaces the terrain of this map, including the world map. It cannot be undone.")}
        </Callout>
      ) : busy ? (
        <Stack>
          <Progress value={progress} />
          <Meta
            left={
              state === b.TerrainState.Downloading
                ? text("TerrainDownloading", "Downloading elevation…")
                : state === b.TerrainState.Planting
                ? `${text("TerrainPlanting", "Planting trees…")} ${trees}`
                : text("TerrainBuilding", "Building terrain…")
            }
            right={`${Math.round(progress * 100)}%`}
          />
          {state === b.TerrainState.Downloading && (
            <Button kind="ghost" wide onClick={b.cancelTerrain}>
              {text("Cancel", "Cancel")}
            </Button>
          )}
        </Stack>
      ) : (
        <Stack>
          <Caption>{text("TerrainSmoothing", "Terrain smoothing")}</Caption>
          <Segmented options={[0, 1, 2]} value={smoothing} format={(i) => densityNames[i]} onSelect={setSmoothing} />
          <Hint>{text("TerrainSmoothingHint", "Softens flat ground and keeps hills and mountains. More smoothing looks cleaner and less exact.")}</Hint>
          <ToggleRow label={text("TerrainLakes", "Lakes and sea")} value={water} onChange={setWater} />
          <ToggleRow label={text("TerrainRiversToggle", "Rivers")} value={withRivers} onChange={setWithRivers} />
          <ToggleRow label={text("TerrainTrees", "Trees")} value={withTrees} onChange={setWithTrees} />
          {withTrees && (
            <>
              <Caption>{text("TerrainDensity", "Tree density")}</Caption>
              <Segmented options={[0, 1, 2]} value={density} format={(i) => densityNames[i]} onSelect={setDensity} />
            </>
          )}
          <div className={styles.generate}>
            <Button kind="primary" wide onClick={() => setConfirming(true)}>
              {state === b.TerrainState.Done ? text("TerrainAgain", "Generate again") : text("TerrainGenerate", "Generate terrain here")}
            </Button>
          </div>
        </Stack>
      )}
      {(water || withRivers || withTrees) && !busy && !confirming && state !== b.TerrainState.Done && (
        <Hint>{text("TerrainLakesHint", "Sea, lakes and rivers come from OpenStreetMap, trees from ESA WorldCover. Water sources are placed for you.")}</Hint>
      )}
      {state === b.TerrainState.Done && message && <Hint warning>{text(message[0], message[1])}</Hint>}
      {state === b.TerrainState.Failed && message && <Hint warning>{text(message[0], message[1])}</Hint>}
      {state === b.TerrainState.Done && (
        <Stack last>
          <LabelRow label={text("TerrainReal", "Real heights")} value={`${metres(lowest)} – ${metres(highest)}`} />
          <LabelRow label={text("TerrainWater", "Editor sea level")} value={metres(waterLevel)} />
          <LabelRow label={text("TerrainLowestGame", "Lowest land in game")} value={metres(lowestInGame)} />
          <LabelRow label={text("TerrainScale", "Height scale")} value={metres(heightScale)} />
          {(lakes > 0 || rivers > 0) && (
            <LabelRow label={text("TerrainWaterFound", "Lakes · rivers · sources")} value={`${lakes} · ${rivers} · ${sources}`} />
          )}
          {trees > 0 && <LabelRow label={text("TerrainTreesPlaced", "Trees placed")} value={`${trees}`} />}
          <Hint>{text("TerrainCredit", "Elevation: Terrain Tiles on AWS (SRTM, GMTED, ETOPO1 and others). Trees: ESA WorldCover 2021 (CC-BY 4.0). Water: © OpenStreetMap contributors, OpenMapTiles, OpenFreeMap.")}</Hint>
        </Stack>
      )}
    </>
  );
};

// Generating terrain under a running city would break it: maps are made in the editor.
const TerrainEditorOnly = () => {
  const text = useText();
  return (
    <Callout>
      <strong>{text("TerrainEditorOnlyTitle", "Terrain is only available in the Map Editor.")}</strong>
      <br />
      {text(
        "TerrainEditorOnly",
        "It replaces the whole map, which would damage a city in progress. Create the map in the Editor, then build your city on it. The Overlay works in both."
      )}
    </Callout>
  );
};

// ---- Roads --------------------------------------------------------------------------

const Roads = () => {
  const text = useText();
  const state = useValue(b.roadState$);
  const progress = useValue(b.roadProgress$);
  const problem = useValue(b.roadProblem$);
  const segments = useValue(b.roadSegments$);
  const bridges = useValue(b.roadBridges$);
  const tunnels = useValue(b.roadTunnels$);
  const skipped = useValue(b.roadSkipped$);
  const named = useValue(b.roadNamed$);
  const [names, setNames] = useState(true);
  const zoom = useValue(b.roadZoom$);
  const canRemove = useValue(b.roadCanRemove$);
  const terrainState = useValue(b.terrainState$);
  const [detail, setDetail] = useState(1);
  const [confirming, setConfirming] = useState<"" | "build" | "remove">("");
  const detailNames = [text("RoadsMain", "Main roads"), text("RoadsStreets", "+ Streets"), text("RoadsAll", "+ Service")];
  const detailHints = [
    text("RoadsMainHint", "Motorways, their ramps, and the main through roads."),
    text("RoadsStreetsHint", "Adds every ordinary street. A dense city centre takes a while to build."),
    text("RoadsAllHint", "Adds service roads, alleys and dirt tracks too."),
  ];
  const busy =
    state === b.RoadState.Downloading || state === b.RoadState.Planning || state === b.RoadState.Placing || state === b.RoadState.Removing;
  const terrainBusy =
    terrainState === b.TerrainState.Downloading || terrainState === b.TerrainState.Building || terrainState === b.TerrainState.Planting;
  const problems: Record<string, [string, string]> = {
    Network: ["RoadsNetwork", "Road data did not download. Check the connection and try again."],
    Build: ["RoadsBuild", "The roads could not be built. See MapOverlay.log."],
    NoRoads: ["RoadsNone", "No roads of this kind were found in the playable area."],
    NoPrefab: ["RoadsNoPrefab", "The game's road types were not found. See MapOverlay.log."],
  };
  const message = problems[problem];

  return (
    <>
      <Stack>
        <Hint>
          {text(
            "RoadsHint",
            "Builds the real roads of this place from OpenStreetMap over the playable area, where the Overlay map shows them: same centre, rotation and scale. Junctions, one-way streets, bridges and tunnels are included."
          )}
        </Hint>
      </Stack>
      {confirming === "build" ? (
        <Callout
          actions={
            <>
              <Button onClick={() => setConfirming("")}>{text("Cancel", "Cancel")}</Button>
              <Button
                kind="primary"
                onClick={() => {
                  setConfirming("");
                  b.buildRoads(detail | (names ? 4 : 0));
                }}
              >
                {text("RoadsBuildConfirm", "Build roads")}
              </Button>
            </>
          }
        >
          {text(
            "RoadsConfirm",
            "This adds roads to the map and does not remove the ones already there. The game freezes while it builds them, up to a few minutes for a whole city. Save first."
          )}
        </Callout>
      ) : confirming === "remove" ? (
        <Callout
          actions={
            <>
              <Button onClick={() => setConfirming("")}>{text("Cancel", "Cancel")}</Button>
              <Button
                kind="primary"
                onClick={() => {
                  setConfirming("");
                  b.removeRoads();
                }}
              >
                {text("RoadsRemove", "Remove")}
              </Button>
            </>
          }
        >
          {text("RoadsRemoveConfirm", "This removes every road added since this map was loaded, including any you drew by hand since then.")}
        </Callout>
      ) : busy ? (
        <Stack>
          <Progress value={progress} />
          <Meta
            left={
              state === b.RoadState.Downloading
                ? text("RoadsDownloading", "Downloading road data…")
                : state === b.RoadState.Planning
                ? text("RoadsPlanning", "Working out junctions…")
                : state === b.RoadState.Removing
                ? text("RoadsRemoving", "Removing roads…")
                : text("RoadsPlacing", "Building roads…")
            }
            right={`${Math.round(progress * 100)}%`}
          />
          {state === b.RoadState.Downloading && (
            <div className={styles.generate}>
              <Button kind="ghost" wide onClick={b.cancelRoads}>
                {text("Cancel", "Cancel")}
              </Button>
            </div>
          )}
        </Stack>
      ) : (
        <Stack>
          <Caption>{text("RoadsDetail", "Which roads")}</Caption>
          <Segmented options={[0, 1, 2]} value={detail} format={(i) => detailNames[i]} onSelect={setDetail} />
          <Hint>{detailHints[detail]}</Hint>
          <div className={styles.generate}>
            <ToggleRow label={text("RoadsNames", "Real street names")} value={names} onChange={setNames} />
          </div>
          <div className={styles.generate}>
            {terrainBusy ? (
              <Hint>{text("RoadsWaitTerrain", "Wait for the terrain to finish first.")}</Hint>
            ) : canRemove ? (
              <Hint>{text("RoadsRemoveFirst", "Remove the generated roads below before generating again.")}</Hint>
            ) : (
              <Button kind="primary" wide onClick={() => setConfirming("build")}>
                {state === b.RoadState.Done ? text("RoadsAgain", "Generate again") : text("RoadsGenerate", "Generate roads here")}
              </Button>
            )}
          </div>
        </Stack>
      )}
      {!busy && confirming === "" && state !== b.RoadState.Done && (
        <Hint>
          {text(
            "RoadsOrderHint",
            "Generate the terrain first: roads follow the ground as it is now. Lane counts are not in the data, so widths follow each road's importance."
          )}
        </Hint>
      )}
      {state === b.RoadState.Failed && message && <Hint warning>{text(message[0], message[1])}</Hint>}
      {state === b.RoadState.Done && (
        <Stack>
          <LabelRow label={text("RoadsSegments", "Road segments")} value={`${segments}`} />
          <LabelRow label={text("RoadsBridgesTunnels", "Bridges · tunnels")} value={`${bridges} · ${tunnels}`} />
          {named > 0 && <LabelRow label={text("RoadsNamed", "Streets with real names")} value={`${named}`} />}
          {skipped > 0 && <LabelRow label={text("RoadsSkipped", "Left out")} value={`${skipped}`} />}
          {zoom < 14 && <Hint warning>{text("RoadsCoarse", "At this map scale the data is coarser: small streets are missing.")}</Hint>}
          <Hint>{text("RoadsCredit", "Roads: © OpenStreetMap contributors, OpenMapTiles, OpenFreeMap.")}</Hint>
        </Stack>
      )}
      {canRemove && !busy && confirming === "" && (
        <Stack>
          <Button kind="ghost" wide onClick={() => setConfirming("remove")}>
            {text("RoadsRemoveAll", "Remove generated roads")}
          </Button>
        </Stack>
      )}
    </>
  );
};

// ---- Footer -------------------------------------------------------------------------

const Status = () => {
  const text = useText();
  const ready = useValue(b.tilesReady$);
  const total = useValue(b.tilesTotal$);
  const failed = useValue(b.tilesFailed$);
  const problem = useValue(b.problem$);
  const attribution = useValue(b.attribution$);
  const source = useValue(b.source$);
  const problems: Record<string, [string, string]> = {
    Network: ["ProblemNetwork", "Some tiles did not download."],
    Blocked: ["ProblemBlocked", "The server asked to wait."],
    BadUrl: ["ProblemBadUrl", "Enter a tile URL with {z}, {x} and {y} in Map → Custom."],
  };
  const message = problems[problem];

  return (
    <>
      {message && (
        <div className={styles.footerWarning}>
          <Hint warning>{text(message[0], message[1])}</Hint>
        </div>
      )}
      <Progress value={total > 0 ? ready / total : 0} />
      <Meta
        left={`${text("Tiles", "Tiles")} ${ready}/${total}${failed > 0 ? ` · ${failed} ${text("Failed", "failed")}` : ""}`}
        right={source === 2 ? text("CustomAttribution", "Credit your tile provider") : attribution}
      />
    </>
  );
};

// Appended to "Game": rendered over the game view while open.
export const OverlayPanel = () => {
  const open = usePanelOpen();
  const visible = useValue(b.visible$);
  const opacity = useValue(b.opacity$);
  const lat = useValue(b.lat$);
  const lon = useValue(b.lon$);
  const inEditor = useValue(b.inEditor$);
  const active = useValue(b.activeProject$);
  const locked = useValue(b.projectLocked$);
  const terrainState = useValue(b.terrainState$);
  const roadState = useValue(b.roadState$);
  const busy =
    terrainState === b.TerrainState.Downloading ||
    terrainState === b.TerrainState.Building ||
    terrainState === b.TerrainState.Planting ||
    roadState === b.RoadState.Downloading ||
    roadState === b.RoadState.Planning ||
    roadState === b.RoadState.Placing ||
    roadState === b.RoadState.Removing;
  const text = useText();
  const tab = useSyncExternalStore(b.tabStore.subscribe, b.tabStore.get);
  const selectTab = (next: b.PanelTab) => {
    // Dragging belongs to the Overlay.
    if (next !== "overlay") b.setDragging(false);
    b.tabStore.set(next);
  };
  if (!open) return null;

  // In the editor it opens beside the side menu and clear of the tools at the bottom.
  const style = inEditor ? { top: "58rem", left: "392rem", width: "372rem" } : { top: "70rem", left: "10rem", width: "372rem" };

  // Projects come first: until one is open there is nothing else to do.
  if (!active) {
    return (
      <ModPanel
        icon={icon}
        title={text("Title", "Real World Maps")}
        subtitle={text("ProjectsSubtitle", "Real maps for your cities")}
        onClose={() => b.panelStore.set(false)}
        style={style}
        dragKey={inEditor ? "editor" : "game"}
        bodyMaxHeight="700rem"
      >
        <ProjectHome busy={busy} />
      </ModPanel>
    );
  }

  return (
    <ModPanel
      icon={icon}
      title={text("Title", "Real World Maps")}
      subtitle={`${lat.toFixed(5)}, ${lon.toFixed(5)}`}
      actions={
        <Tooltip tooltip={text("Visible", "Show map")}>
          <div className={styles.headerSwitch}>
            <Switch value={visible} onChange={b.setVisible} />
          </div>
        </Tooltip>
      }
      footer={<Status />}
      onClose={() => b.panelStore.set(false)}
      style={style}
      dragKey={inEditor ? "editor" : "game"}
      bodyMaxHeight="700rem"
    >
      <ProjectBar busy={busy} />
      <Tabs
        options={["overlay", "terrain", "roads"] as b.PanelTab[]}
        value={tab}
        format={(t) => (t === "overlay" ? text("TabOverlay", "Overlay") : t === "terrain" ? text("TabTerrain", "Terrain") : text("TabRoads", "Roads"))}
        dimmed={(t) => t === "terrain" && !inEditor}
        onSelect={selectTab}
      />
      {tab === "overlay" ? (
        <>
          {!locked && <SearchBox />}
          <Slider label={text("Opacity", "Opacity")} value={opacity} format={(v) => `${v}%`} onCommit={b.setOpacity} />
          {!locked && <Position />}
          {!locked && <Scale />}
          <Loading />
        </>
      ) : tab === "roads" ? (
        <Roads />
      ) : inEditor ? (
        <Terrain />
      ) : (
        <TerrainEditorOnly />
      )}
    </ModPanel>
  );
};

// Wraps the editor's row of tools at the bottom of the screen, adding the mod's button to it
// with the theme of the native buttons.
export const withEditorButton =
  (theme: Record<string, string>) =>
  (Toolbar: ComponentType<any>) =>
  ({ className, ...props }: any) => {
    const open = usePanelOpen();
    const text = useText();
    return (
      <div className={cx(className, styles.toolbarHost)}>
        <Toolbar {...props} />
        <Tooltip tooltip={text("Title", "Real World Maps")}>
          <button className={cx(theme.button, styles.toolbarButton, open && "selected")} onClick={() => b.panelStore.set(!open)}>
            <img className={styles.toolbarIcon} src={icon} />
          </button>
        </Tooltip>
      </div>
    );
  };

// Only if the editor's row of tools cannot be found.
export const EditorFloatingButton = () => (
  <div className={styles.editorButton}>
    <ToolbarButton />
  </div>
);
