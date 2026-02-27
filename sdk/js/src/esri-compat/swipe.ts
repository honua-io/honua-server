import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface SwipeCompatOptions {
  view?: unknown;
  container?: unknown;
  leadingLayers?: readonly unknown[];
  trailingLayers?: readonly unknown[];
  position?: number;
  eventBus?: CompatEventBus;
}

export class SwipeCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public leadingLayers: unknown[];
  public trailingLayers: unknown[];
  public position: number;

  public constructor(options: SwipeCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.view, options.leadingLayers, options.trailingLayers) ??
      new CompatEventBus();
    this.leadingLayers = options.leadingLayers ? [...options.leadingLayers] : [];
    this.trailingLayers = options.trailingLayers ? [...options.trailingLayers] : [];
    this.position = normalizePosition(options.position ?? 50);
  }

  public setPosition(position: number): void {
    this.position = normalizePosition(position);
    this.eventBus.emit("swipe.position-changed", { position: this.position }, this);
  }

  public setLeadingLayers(layers: readonly unknown[]): void {
    this.leadingLayers = [...layers];
    this.eventBus.emit("swipe.layers-changed", { side: "leading", count: this.leadingLayers.length }, this);
  }

  public setTrailingLayers(layers: readonly unknown[]): void {
    this.trailingLayers = [...layers];
    this.eventBus.emit("swipe.layers-changed", { side: "trailing", count: this.trailingLayers.length }, this);
  }
}

function normalizePosition(value: number): number {
  if (!Number.isFinite(value)) {
    return 50;
  }
  return Math.min(100, Math.max(0, Math.trunc(value)));
}
