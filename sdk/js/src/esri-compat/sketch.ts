import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export type SketchCreationModeCompat = "single" | "update" | "continuous";
export type SketchToolCompat = "point" | "polyline" | "polygon" | "rectangle" | "circle";

export interface SketchCreateOptionsCompat {
  mode?: "click" | "freehand" | "hybrid";
}

export interface SketchUpdateOptionsCompat {
  tool?: "move" | "transform" | "reshape";
  enableRotation?: boolean;
  enableScaling?: boolean;
  multipleSelectionEnabled?: boolean;
  toggleToolOnClick?: boolean;
}

export interface SketchCompatOptions {
  view?: unknown;
  layer?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  updateOnGraphicClick?: boolean;
  creationMode?: SketchCreationModeCompat;
  defaultCreateOptions?: Partial<SketchCreateOptionsCompat>;
  defaultUpdateOptions?: Partial<SketchUpdateOptionsCompat>;
}

export interface SketchCreateResultCompat {
  state: "complete" | "cancel";
  tool: SketchToolCompat;
  graphic?: unknown;
}

export type SketchLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface SketchHandleCompat {
  remove(): void;
}

export class SketchCompat {
  public readonly view: unknown;
  public readonly layer: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly updateOnGraphicClick: boolean;
  public readonly creationMode: SketchCreationModeCompat;
  public readonly defaultCreateOptions: Partial<SketchCreateOptionsCompat>;
  public readonly defaultUpdateOptions: Partial<SketchUpdateOptionsCompat>;
  public loaded: boolean;
  public loadStatus: SketchLoadStatusCompat;
  public state: "ready" | "active";
  public activeTool: SketchToolCompat | undefined;
  public activeCreateOptions: Partial<SketchCreateOptionsCompat> | undefined;
  public activeUpdateGraphics: unknown[];
  public activeUpdateOptions: Partial<SketchUpdateOptionsCompat> | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: SketchCompatOptions = {}) {
    this.view = options.view;
    this.layer = options.layer;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view, options.layer) ?? new CompatEventBus();
    this.updateOnGraphicClick = options.updateOnGraphicClick ?? true;
    this.creationMode = options.creationMode ?? "single";
    this.defaultCreateOptions = { ...(options.defaultCreateOptions ?? {}) };
    this.defaultUpdateOptions = { ...(options.defaultUpdateOptions ?? {}) };
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.state = "ready";
    this.activeTool = undefined;
    this.activeCreateOptions = undefined;
    this.activeUpdateGraphics = [];
    this.activeUpdateOptions = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<SketchCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("sketch.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("sketch.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: SketchCompat) => void): Promise<SketchCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SketchHandleCompat {
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

  public create(tool: SketchToolCompat, options?: Partial<SketchCreateOptionsCompat>): void {
    this.state = "active";
    this.notifyWatchers("state", this.state);
    this.activeTool = tool;
    this.notifyWatchers("activeTool", this.activeTool);
    this.activeCreateOptions = {
      ...this.defaultCreateOptions,
      ...(options ?? {}),
    };
    this.notifyWatchers("activeCreateOptions", this.activeCreateOptions);
    this.eventBus.emit(
      "sketch.create-started",
      {
        tool,
        creationMode: this.creationMode,
        options: this.activeCreateOptions,
      },
      this,
    );
  }

  public complete(graphic?: unknown): SketchCreateResultCompat | undefined {
    if (this.state !== "active" || !this.activeTool) {
      return undefined;
    }

    const tool = this.activeTool;
    const createdGraphic = graphic ?? {};
    appendGraphic(this.layer, createdGraphic);
    this.clearActiveState();
    this.eventBus.emit(
      "sketch.create-completed",
      {
        tool,
        graphic: createdGraphic,
        layerGraphicCount: getLayerGraphics(this.layer).length,
      },
      this,
    );
    return {
      state: "complete",
      tool,
      graphic: createdGraphic,
    };
  }

  public cancel(): SketchCreateResultCompat | undefined {
    if (this.state !== "active" || !this.activeTool) {
      return undefined;
    }

    const tool = this.activeTool;
    this.clearActiveState();
    this.eventBus.emit("sketch.create-cancelled", { tool }, this);
    return {
      state: "cancel",
      tool,
    };
  }

  public update(
    graphics: unknown | readonly unknown[],
    options?: Partial<SketchUpdateOptionsCompat>,
  ): readonly unknown[] {
    const normalizedGraphics = normalizeGraphicsInput(graphics);
    this.activeUpdateGraphics = [...normalizedGraphics];
    this.notifyWatchers("activeUpdateGraphics", this.activeUpdateGraphics);
    this.activeUpdateOptions = {
      ...this.defaultUpdateOptions,
      ...(options ?? {}),
    };
    this.notifyWatchers("activeUpdateOptions", this.activeUpdateOptions);
    this.eventBus.emit(
      "sketch.update-started",
      {
        count: normalizedGraphics.length,
        options: this.activeUpdateOptions,
      },
      this,
    );
    return this.activeUpdateGraphics;
  }

  public delete(graphics?: unknown | readonly unknown[]): number {
    const targets =
      graphics !== undefined ? normalizeGraphicsInput(graphics) : [...this.activeUpdateGraphics];
    let removed = 0;
    for (const target of targets) {
      if (removeGraphic(this.layer, target)) {
        removed += 1;
      }
    }

    if (removed > 0) {
      this.eventBus.emit(
        "sketch.graphics-deleted",
        {
          count: removed,
          layerGraphicCount: getLayerGraphics(this.layer).length,
        },
        this,
      );
    }

    if (this.activeUpdateGraphics.length > 0) {
      this.activeUpdateGraphics = this.activeUpdateGraphics.filter(
        (graphic) => !targets.some((target) => target === graphic),
      );
      this.notifyWatchers("activeUpdateGraphics", this.activeUpdateGraphics);
    }
    return removed;
  }

  public reset(): void {
    this.clearActiveState();
    this.activeUpdateGraphics = [];
    this.notifyWatchers("activeUpdateGraphics", this.activeUpdateGraphics);
    this.activeUpdateOptions = undefined;
    this.notifyWatchers("activeUpdateOptions", this.activeUpdateOptions);
    this.eventBus.emit("sketch.reset", undefined, this);
  }

  private clearActiveState(): void {
    this.state = "ready";
    this.notifyWatchers("state", this.state);
    this.activeTool = undefined;
    this.notifyWatchers("activeTool", this.activeTool);
    this.activeCreateOptions = undefined;
    this.notifyWatchers("activeCreateOptions", this.activeCreateOptions);
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

interface GraphicsLayerLike {
  graphics?: unknown[];
  add?(graphic: unknown): unknown;
  remove?(graphic: unknown): unknown;
}

function normalizeGraphicsInput(graphics: unknown | readonly unknown[]): unknown[] {
  return Array.isArray(graphics) ? [...graphics] : [graphics];
}

function appendGraphic(layer: unknown, graphic: unknown): void {
  if (!isGraphicsLayerLike(layer)) {
    return;
  }

  if (typeof layer.add === "function") {
    layer.add(graphic);
    return;
  }

  if (Array.isArray(layer.graphics)) {
    layer.graphics.push(graphic);
  }
}

function removeGraphic(layer: unknown, graphic: unknown): boolean {
  if (!isGraphicsLayerLike(layer)) {
    return false;
  }

  if (typeof layer.remove === "function") {
    const beforeCount = getLayerGraphics(layer).length;
    const result = layer.remove(graphic);
    if (result !== undefined) {
      return true;
    }
    return getLayerGraphics(layer).length < beforeCount;
  }

  if (!Array.isArray(layer.graphics)) {
    return false;
  }

  const index = layer.graphics.findIndex((candidate) => candidate === graphic);
  if (index < 0) {
    return false;
  }
  layer.graphics.splice(index, 1);
  return true;
}

function getLayerGraphics(layer: unknown): unknown[] {
  if (!isGraphicsLayerLike(layer) || !Array.isArray(layer.graphics)) {
    return [];
  }
  return layer.graphics;
}

function isGraphicsLayerLike(value: unknown): value is GraphicsLayerLike {
  return typeof value === "object" && value !== null;
}
