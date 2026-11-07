import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import { FloatingButton, Tooltip } from "cs2/ui";
import {
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
      tooltipLabel={text("Title", "Map Overlay")}
      onSelect={() => b.panelStore.set(!open)}
    />
  );
};

const formatPercent = (v: number) => `${Math.round(v * 10) / 10}%`;
const formatKm = (km: number) => `${km >= 100 ? km.toFixed(1) : km.toFixed(2)} km`;

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
              ▲
            </button>
          </div>
          <div className={styles.padRow}>
            <button className={styles.padButton} onClick={() => b.nudge(-step, 0)}>
              ◀
            </button>
            <Tooltip tooltip={text("BringHere", "Bring centre here")}>
              <button className={cx(styles.padButton, styles.padCenter)} onClick={() => b.bringHere()}>
                ◎
              </button>
            </Tooltip>
            <button className={styles.padButton} onClick={() => b.nudge(step, 0)}>
              ▶
            </button>
          </div>
          <div className={styles.padRow}>
            <button className={styles.padButton} onClick={() => b.nudge(0, -step)}>
              ▼
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
  const text = useText();
  if (!open) return null;

  return (
    <ModPanel
      icon={icon}
      title={text("Title", "Map Overlay")}
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
      style={{ top: "70rem", left: "10rem", width: "372rem" }}
      bodyMaxHeight="700rem"
    >
      <SearchBox />
      <Slider label={text("Opacity", "Opacity")} value={opacity} format={(v) => `${v}%`} onCommit={b.setOpacity} />
      <Position />
      <Scale />
      <Loading />
    </ModPanel>
  );
};
