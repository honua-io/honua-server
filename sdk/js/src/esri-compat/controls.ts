import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface HomeCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  viewpoint?: HomeViewpointCompat;
}

export interface HomeViewpointCompat {
  center?: unknown;
  zoom?: number;
}

export type ControlLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface ControlHandleCompat {
  remove(): void;
}

export class HomeCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public viewpoint: HomeViewpointCompat;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: HomeCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.viewpoint = options.viewpoint ?? {
      center: extractViewCenter(options.view),
      zoom: extractViewZoom(options.view),
    };
    this.watchListeners = new Map();
  }

  public async load(): Promise<HomeCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("home.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("home.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: HomeCompat) => void): Promise<HomeCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public async go(): Promise<void> {
    const target = {
      center: this.viewpoint.center,
      zoom: this.viewpoint.zoom,
    };

    if (isGoToProvider(this.view)) {
      await this.view.goTo(target);
    } else {
      setViewCenterZoom(this.view, target);
    }

    this.eventBus.emit("home.go", target, this);
  }

  public reset(): void {
    this.viewpoint = {
      center: extractViewCenter(this.view),
      zoom: extractViewZoom(this.view),
    };
    this.notifyWatchers("viewpoint", this.viewpoint);
    this.eventBus.emit("home.reset", this.viewpoint, this);
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export interface BasemapToggleCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  nextBasemap?: unknown;
  eventBus?: CompatEventBus;
}

export class BasemapToggleCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public activeBasemap: unknown;
  public nextBasemap: unknown;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: BasemapToggleCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, this.map) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.activeBasemap = extractMapBasemap(this.map);
    this.nextBasemap = options.nextBasemap;
    this.watchListeners = new Map();
    this.subscriptions = [
      this.eventBus.on("map.basemap-changed", (event) => {
        this.activeBasemap = extractPayloadBasemap(event.payload);
        this.notifyWatchers("activeBasemap", this.activeBasemap);
      }),
    ];
  }

  public async load(): Promise<BasemapToggleCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-toggle.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("basemap-toggle.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: BasemapToggleCompat) => void): Promise<BasemapToggleCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public toggle(): unknown {
    const currentMap = this.map;
    if (!isRecord(currentMap)) {
      return undefined;
    }

    const previous = currentMap.basemap;
    setMapBasemap(currentMap, this.nextBasemap, this.eventBus, this);
    this.activeBasemap = extractMapBasemap(currentMap);
    this.notifyWatchers("activeBasemap", this.activeBasemap);
    this.nextBasemap = previous;
    this.notifyWatchers("nextBasemap", this.nextBasemap);
    this.eventBus.emit(
      "basemap.toggle",
      {
        activeBasemap: this.activeBasemap,
        nextBasemap: this.nextBasemap,
      },
      this,
    );
    return this.activeBasemap;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export type ScaleBarUnitCompat = "metric" | "imperial" | "dual";

export interface ScaleBarCompatOptions {
  view?: unknown;
  container?: unknown;
  unit?: ScaleBarUnitCompat;
  eventBus?: CompatEventBus;
}

export class ScaleBarCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public unit: ScaleBarUnitCompat;
  public scale: number | undefined;
  public text: string;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: ScaleBarCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.unit = options.unit ?? "metric";
    this.scale = undefined;
    this.text = "";
    this.watchListeners = new Map();
    this.subscriptions = [
      this.eventBus.on("view.go-to", () => {
        this.refresh();
      }),
    ];
    this.refresh();
  }

  public async load(): Promise<ScaleBarCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("scalebar.loading", undefined, this);
    this.refresh();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("scalebar.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: ScaleBarCompat) => void): Promise<ScaleBarCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public refresh(): string {
    const zoom = extractViewZoom(this.view);
    if (zoom === undefined) {
      this.scale = undefined;
      this.notifyWatchers("scale", this.scale);
      this.text = "";
      this.notifyWatchers("text", this.text);
      return this.text;
    }

    const mapScale = 591657527.591555 / 2 ** zoom;
    this.scale = mapScale;
    this.notifyWatchers("scale", this.scale);
    this.text = buildScaleBarText(mapScale, this.unit);
    this.notifyWatchers("text", this.text);
    this.eventBus.emit("scalebar.updated", { scale: mapScale, text: this.text, unit: this.unit }, this);
    return this.text;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export interface LocateCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  zoom?: number;
  locateProvider?: () => Promise<LocatePositionCompat>;
}

export interface LocatePositionCompat {
  coords: {
    latitude: number;
    longitude: number;
    accuracy?: number;
  };
}

export interface CompassCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
}

export interface ZoomCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  layout?: "vertical" | "horizontal";
}

export interface FullscreenCompatOptions {
  view?: unknown;
  container?: unknown;
  element?: unknown;
  eventBus?: CompatEventBus;
}

export interface AttributionCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  itemDelimiter?: string;
  attributions?: readonly string[];
}

export class LocateCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public readonly zoom: number | undefined;
  public lastPosition: LocatePositionCompat | undefined;

  private readonly locateProvider: () => Promise<LocatePositionCompat>;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: LocateCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.zoom = options.zoom;
    this.lastPosition = undefined;
    this.locateProvider = options.locateProvider ?? getDefaultLocateProvider();
    this.watchListeners = new Map();
  }

  public async load(): Promise<LocateCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("locate.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("locate.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: LocateCompat) => void): Promise<LocateCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public async locate(): Promise<LocatePositionCompat> {
    this.eventBus.emit("locate.start", undefined, this);

    try {
      const position = await this.locateProvider();
      this.lastPosition = position;
      this.notifyWatchers("lastPosition", this.lastPosition);
      const center: [number, number] = [position.coords.longitude, position.coords.latitude];
      const target = {
        center,
        zoom: this.zoom,
      };
      if (isGoToProvider(this.view)) {
        await this.view.goTo(target);
      } else {
        setViewCenterZoom(this.view, target);
      }
      this.eventBus.emit(
        "locate.success",
        {
          position,
          center,
          zoom: this.zoom,
        },
        this,
      );
      return position;
    } catch (error) {
      this.eventBus.emit("locate.error", { error }, this);
      throw error;
    }
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export class CompassCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public orientation: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: CompassCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.orientation = extractViewRotation(options.view) ?? 0;
    this.watchListeners = new Map();
  }

  public async load(): Promise<CompassCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("compass.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("compass.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: CompassCompat) => void): Promise<CompassCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public rotateTo(rotation: number): number {
    const next = Number.isFinite(rotation) ? rotation : this.orientation;
    this.orientation = next;
    this.notifyWatchers("orientation", this.orientation);
    if (isRecord(this.view)) {
      this.view.rotation = next;
    }
    this.eventBus.emit("compass.rotated", { rotation: next }, this);
    return this.orientation;
  }

  public reset(): number {
    const rotation = this.rotateTo(0);
    this.eventBus.emit("compass.reset", { rotation }, this);
    return rotation;
  }

  public goToNorth(): number {
    return this.reset();
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export class ZoomCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public readonly layout: "vertical" | "horizontal";
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: ZoomCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.layout = options.layout ?? "vertical";
    this.watchListeners = new Map();
  }

  public async load(): Promise<ZoomCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("zoom.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("zoom.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: ZoomCompat) => void): Promise<ZoomCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public zoomIn(step = 1): number | undefined {
    return this.adjustZoom(Math.abs(step));
  }

  public zoomOut(step = 1): number | undefined {
    return this.adjustZoom(-Math.abs(step));
  }

  private adjustZoom(delta: number): number | undefined {
    if (!isRecord(this.view) || typeof this.view.zoom !== "number" || !Number.isFinite(this.view.zoom)) {
      return undefined;
    }

    const next = this.view.zoom + delta;
    this.view.zoom = next;
    this.notifyWatchers("zoom", next);
    this.eventBus.emit("zoom.changed", { zoom: next, delta }, this);
    return next;
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export class FullscreenCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly element: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public active: boolean;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: FullscreenCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.element = options.element;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.active = false;
    this.watchListeners = new Map();
  }

  public async load(): Promise<FullscreenCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("fullscreen.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("fullscreen.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: FullscreenCompat) => void): Promise<FullscreenCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public enter(): void {
    if (this.active) {
      return;
    }
    this.active = true;
    this.notifyWatchers("active", this.active);
    this.eventBus.emit("fullscreen.changed", { active: true }, this);
  }

  public exit(): void {
    if (!this.active) {
      return;
    }
    this.active = false;
    this.notifyWatchers("active", this.active);
    this.eventBus.emit("fullscreen.changed", { active: false }, this);
  }

  public toggle(force?: boolean): boolean {
    const next = force ?? !this.active;
    if (next) {
      this.enter();
    } else {
      this.exit();
    }
    return this.active;
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

export class AttributionCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: ControlLoadStatusCompat;
  public itemDelimiter: string;
  public attributions: string[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: AttributionCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.itemDelimiter = options.itemDelimiter ?? " | ";
    this.attributions = options.attributions ? [...options.attributions] : [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<AttributionCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("attribution.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("attribution.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: AttributionCompat) => void): Promise<AttributionCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): ControlHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }
    listeners.add(listener);

    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public addAttribution(value: string): void {
    if (value.trim().length === 0) {
      return;
    }
    this.attributions.push(value);
    this.notifyWatchers("attributions", this.attributions);
    this.notifyWatchers("text", this.getText());
    this.eventBus.emit("attribution.updated", { count: this.attributions.length }, this);
  }

  public removeAttribution(value: string): boolean {
    const index = this.attributions.indexOf(value);
    if (index < 0) {
      return false;
    }
    this.attributions.splice(index, 1);
    this.notifyWatchers("attributions", this.attributions);
    this.notifyWatchers("text", this.getText());
    this.eventBus.emit("attribution.updated", { count: this.attributions.length }, this);
    return true;
  }

  public getText(): string {
    if (this.attributions.length === 0) {
      return "";
    }
    return this.attributions.join(this.itemDelimiter);
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      safeInvokeCompatListener(listener, value);
    }
  }
}

interface GoToProvider {
  goTo(target: { center?: unknown; zoom?: number }): Promise<unknown> | unknown;
}

interface MapBasemapSetter {
  setBasemap(basemap: unknown): void;
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function setViewCenterZoom(view: unknown, target: { center?: unknown; zoom?: number }): void {
  if (!isRecord(view)) {
    return;
  }
  if (target.center !== undefined) {
    view.center = target.center;
  }
  if (typeof target.zoom === "number") {
    view.zoom = target.zoom;
  }
}

function extractViewCenter(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.center;
}

function extractViewZoom(view: unknown): number | undefined {
  if (!isRecord(view) || typeof view.zoom !== "number" || !Number.isFinite(view.zoom)) {
    return undefined;
  }
  return view.zoom;
}

function extractViewMap(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function extractMapBasemap(map: unknown): unknown {
  if (!isRecord(map)) {
    return undefined;
  }
  return map.basemap;
}

function extractPayloadBasemap(payload: unknown): unknown {
  if (!isRecord(payload)) {
    return undefined;
  }
  return payload.basemap;
}

function setMapBasemap(map: unknown, basemap: unknown, eventBus: CompatEventBus, source: unknown): void {
  if (!isRecord(map)) {
    return;
  }
  if (isMapBasemapSetter(map)) {
    map.setBasemap(basemap);
    return;
  }

  map.basemap = basemap;
  eventBus.emit("map.basemap-changed", { basemap }, source);
}

function isMapBasemapSetter(value: unknown): value is MapBasemapSetter {
  return isRecord(value) && typeof value.setBasemap === "function";
}

function buildScaleBarText(scale: number, unit: ScaleBarUnitCompat): string {
  const ratioText = `1:${Math.max(1, Math.round(scale)).toLocaleString("en-US")}`;
  if (unit === "metric") {
    return `${ratioText} | ${formatMetricDistance(scale)}`;
  }
  if (unit === "imperial") {
    return `${ratioText} | ${formatImperialDistance(scale)}`;
  }
  return `${ratioText} | ${formatMetricDistance(scale)} / ${formatImperialDistance(scale)}`;
}

function formatMetricDistance(scale: number): string {
  const meters = Math.max(1, Math.round(scale * 0.00028));
  if (meters >= 1000) {
    return `${Math.round(meters / 1000)} km`;
  }
  return `${meters} m`;
}

function formatImperialDistance(scale: number): string {
  const feet = Math.max(1, Math.round(scale * 0.0009186351706));
  if (feet >= 5280) {
    return `${Math.round(feet / 5280)} mi`;
  }
  return `${feet} ft`;
}

function getDefaultLocateProvider(): () => Promise<LocatePositionCompat> {
  return () =>
    new Promise<LocatePositionCompat>((resolve, reject) => {
      const geolocation = globalThis.navigator?.geolocation;
      if (!geolocation || typeof geolocation.getCurrentPosition !== "function") {
        reject(new Error("Geolocation API is unavailable; provide locateProvider."));
        return;
      }

      geolocation.getCurrentPosition(
        (position) => {
          resolve({
            coords: {
              latitude: position.coords.latitude,
              longitude: position.coords.longitude,
              accuracy: position.coords.accuracy,
            },
          });
        },
        (error) => {
          reject(error);
        },
      );
    });
}

function extractViewRotation(view: unknown): number | undefined {
  if (!isRecord(view)) {
    return undefined;
  }
  const rotation = view.rotation;
  return typeof rotation === "number" && Number.isFinite(rotation) ? rotation : undefined;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
