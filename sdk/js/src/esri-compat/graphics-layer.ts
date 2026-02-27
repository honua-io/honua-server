import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface GraphicsLayerCompatOptions {
  id?: string;
  title?: string;
  graphics?: unknown[];
  visible?: boolean;
  opacity?: number;
  listMode?: "show" | "hide";
  eventBus?: CompatEventBus;
}

export interface GraphicsLayerQueryResult {
  features: unknown[];
}

export type GraphicsLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export class GraphicsLayerCompat {
  public readonly type: "graphics";
  public id: string | undefined;
  public title: string | undefined;
  public visible: boolean;
  public opacity: number;
  public listMode: "show" | "hide";
  public loaded: boolean;
  public loadStatus: GraphicsLayerLoadStatusCompat;
  public readonly eventBus: CompatEventBus;
  private readonly graphicsInternal: unknown[];

  public constructor(options: GraphicsLayerCompatOptions = {}) {
    this.type = "graphics";
    this.id = options.id;
    this.title = options.title;
    this.visible = options.visible ?? true;
    this.opacity = options.opacity ?? 1;
    this.listMode = options.listMode ?? "show";
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.graphicsInternal = Array.isArray(options.graphics) ? [...options.graphics] : [];
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.graphics) ?? new CompatEventBus();
  }

  public async load(): Promise<GraphicsLayerCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.eventBus.emit("graphics-layer.loading", { layerId: this.id }, this);
    this.loaded = true;
    this.loadStatus = "loaded";
    this.eventBus.emit("graphics-layer.loaded", { layerId: this.id }, this);
    return this;
  }

  public async when(callback?: (layer: GraphicsLayerCompat) => void): Promise<GraphicsLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }
    return layer;
  }

  public get graphics(): readonly unknown[] {
    return this.graphicsInternal;
  }

  public add(graphic: unknown, index?: number): unknown {
    if (index === undefined) {
      this.graphicsInternal.push(graphic);
      this.eventBus.emit(
        "graphics-layer.graphic-added",
        { layerId: this.id, graphic, index: this.graphicsInternal.length - 1 },
        this,
      );
      return graphic;
    }

    const insertAt = normalizeInsertIndex(index, this.graphicsInternal.length);
    this.graphicsInternal.splice(insertAt, 0, graphic);
    this.eventBus.emit("graphics-layer.graphic-added", { layerId: this.id, graphic, index: insertAt }, this);
    return graphic;
  }

  public addMany(graphics: readonly unknown[], index?: number): readonly unknown[] {
    if (graphics.length === 0) {
      return [];
    }

    if (index === undefined) {
      const startIndex = this.graphicsInternal.length;
      this.graphicsInternal.push(...graphics);
      this.eventBus.emit(
        "graphics-layer.graphics-added",
        { layerId: this.id, graphics: [...graphics], index: startIndex },
        this,
      );
      return graphics;
    }

    const insertAt = normalizeInsertIndex(index, this.graphicsInternal.length);
    this.graphicsInternal.splice(insertAt, 0, ...graphics);
    this.eventBus.emit(
      "graphics-layer.graphics-added",
      { layerId: this.id, graphics: [...graphics], index: insertAt },
      this,
    );
    return graphics;
  }

  public remove(graphic: unknown): unknown | undefined {
    const index = this.graphicsInternal.indexOf(graphic);
    if (index < 0) {
      return undefined;
    }

    const [removed] = this.graphicsInternal.splice(index, 1);
    this.eventBus.emit("graphics-layer.graphic-removed", { layerId: this.id, graphic: removed, index }, this);
    return removed;
  }

  public removeMany(graphics: readonly unknown[]): unknown[] {
    const removed: unknown[] = [];
    for (const graphic of graphics) {
      const removedGraphic = this.remove(graphic);
      if (removedGraphic !== undefined) {
        removed.push(removedGraphic);
      }
    }
    return removed;
  }

  public removeAll(): void {
    if (this.graphicsInternal.length === 0) {
      return;
    }

    const removed = [...this.graphicsInternal];
    this.graphicsInternal.length = 0;
    this.eventBus.emit("graphics-layer.graphics-cleared", { layerId: this.id, graphics: removed }, this);
  }

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.eventBus.emit("layer.visibility-changed", { layerId: this.id, visible }, this);
  }

  public setOpacity(opacity: number): void {
    this.opacity = opacity;
    this.eventBus.emit("layer.opacity-changed", { layerId: this.id, opacity }, this);
  }

  public async queryFeatures(): Promise<GraphicsLayerQueryResult> {
    return {
      features: [...this.graphicsInternal],
    };
  }

  public async queryFeatureCount(): Promise<number> {
    return this.graphicsInternal.length;
  }
}

function normalizeInsertIndex(index: number, length: number): number {
  const sanitized = Number.isFinite(index) ? Math.trunc(index) : length;
  return Math.min(Math.max(sanitized, 0), length);
}
