import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface PopupCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  autoOpenEnabled?: boolean;
  dockEnabled?: boolean;
  dockOptions?: unknown;
}

export interface PopupOpenOptionsCompat {
  location?: unknown;
  features?: readonly unknown[];
  title?: string;
  content?: unknown;
}

export interface PopupHandleCompat {
  remove(): void;
}

export type PopupLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export class PopupCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: PopupLoadStatusCompat;
  public visible: boolean;
  public location: unknown;
  public features: unknown[];
  public title: string | undefined;
  public content: unknown;
  public autoOpenEnabled: boolean;
  public dockEnabled: boolean;
  public dockOptions: unknown;
  public selectedFeature: unknown;
  public selectedFeatureIndex: number;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PopupCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.title = undefined;
    this.content = undefined;
    this.autoOpenEnabled = options.autoOpenEnabled ?? true;
    this.dockEnabled = options.dockEnabled ?? false;
    this.dockOptions = options.dockOptions;
    this.selectedFeature = undefined;
    this.selectedFeatureIndex = -1;
    this.watchListeners = new Map();
    this.subscriptions = [
      this.eventBus.on("popup.open", () => {
        this.syncFromViewPopup();
      }),
      this.eventBus.on("popup.close", () => {
        this.syncFromViewPopup();
      }),
    ];

    this.syncFromViewPopup();
  }

  public async load(): Promise<PopupCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("popup.loading", undefined, this);
    this.syncFromViewPopup();
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("popup.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: PopupCompat) => void): Promise<PopupCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public open(options: PopupOpenOptionsCompat = {}): void {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup) {
      viewPopup.open(options);
      return;
    }

    this.applyOpenOptions(options);
    this.visible = true;
    this.selectedFeature = this.features[0];
    this.selectedFeatureIndex = this.features.length > 0 ? 0 : -1;
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.notifyWatchers("selectedFeatureIndex", this.selectedFeatureIndex);
    this.eventBus.emit("popup.open", options, this);
  }

  public close(): void {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup) {
      viewPopup.close();
      return;
    }

    if (!this.visible) {
      return;
    }

    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.title = undefined;
    this.content = undefined;
    this.selectedFeature = undefined;
    this.selectedFeatureIndex = -1;
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.notifyWatchers("selectedFeatureIndex", this.selectedFeatureIndex);
    this.eventBus.emit("popup.close", undefined, this);
  }

  public clear(): void {
    this.close();
  }

  public selectFeature(featureOrIndex: unknown | number): unknown {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup?.selectFeature) {
      viewPopup.selectFeature(featureOrIndex);
      this.syncFromViewPopup();
      return this.selectedFeature;
    }

    const index =
      typeof featureOrIndex === "number"
        ? normalizeFeatureIndex(featureOrIndex, this.features.length)
        : this.features.findIndex((feature) => feature === featureOrIndex);
    if (index < 0) {
      return undefined;
    }

    this.applySelection(index);
    return this.selectedFeature;
  }

  public next(): unknown {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup?.next) {
      viewPopup.next();
      this.syncFromViewPopup();
      return this.selectedFeature;
    }

    if (this.features.length === 0) {
      return undefined;
    }
    const current = this.selectedFeatureIndex >= 0 ? this.selectedFeatureIndex : 0;
    this.applySelection(Math.min(current + 1, this.features.length - 1));
    return this.selectedFeature;
  }

  public previous(): unknown {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup?.previous) {
      viewPopup.previous();
      this.syncFromViewPopup();
      return this.selectedFeature;
    }

    if (this.features.length === 0) {
      return undefined;
    }
    const current = this.selectedFeatureIndex >= 0 ? this.selectedFeatureIndex : 0;
    this.applySelection(Math.max(current - 1, 0));
    return this.selectedFeature;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PopupHandleCompat {
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

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.watchListeners.clear();
  }

  private applyOpenOptions(options: PopupOpenOptionsCompat): void {
    this.location = options.location;
    this.features = options.features ? [...options.features] : [];
    this.title = options.title;
    this.content = options.content;
  }

  private syncFromViewPopup(): void {
    const viewPopup = resolveViewPopup(this.view);
    if (!viewPopup) {
      return;
    }

    this.visible = viewPopup.visible;
    this.location = viewPopup.location;
    this.features = [...viewPopup.features];
    this.title = viewPopup.title;
    this.content = viewPopup.content;
    this.selectedFeature = viewPopup.selectedFeature ?? this.features[0];
    this.selectedFeatureIndex =
      typeof viewPopup.selectedFeatureIndex === "number"
        ? normalizeFeatureIndex(viewPopup.selectedFeatureIndex, this.features.length)
        : this.features.findIndex((feature) => feature === this.selectedFeature);
    if (this.selectedFeatureIndex < 0) {
      this.selectedFeatureIndex = this.features.length > 0 ? 0 : -1;
    }
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.notifyWatchers("selectedFeatureIndex", this.selectedFeatureIndex);
  }

  private applySelection(index: number): void {
    const normalizedIndex = normalizeFeatureIndex(index, this.features.length);
    if (normalizedIndex < 0) {
      return;
    }

    this.selectedFeatureIndex = normalizedIndex;
    this.selectedFeature = this.features[normalizedIndex];
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.notifyWatchers("selectedFeatureIndex", this.selectedFeatureIndex);
    this.eventBus.emit(
      "popup.selected-feature-changed",
      {
        selectedFeature: this.selectedFeature,
        selectedFeatureIndex: this.selectedFeatureIndex,
      },
      this,
    );
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(value);
    }
  }
}

interface ViewPopupLike {
  visible: boolean;
  location: unknown;
  features: unknown[];
  selectedFeatureIndex?: number;
  selectedFeature?: unknown;
  title: string | undefined;
  content: unknown;
  open(options?: PopupOpenOptionsCompat): void;
  close(): void;
  selectFeature?(featureOrIndex: unknown | number): unknown;
  next?(): unknown;
  previous?(): unknown;
}

function resolveViewPopup(view: unknown): ViewPopupLike | undefined {
  if (!isRecord(view) || !isRecord(view.popup)) {
    return undefined;
  }

  const popup = view.popup;
  if (typeof popup.open !== "function" || typeof popup.close !== "function") {
    return undefined;
  }
  if (typeof popup.visible !== "boolean" || !Array.isArray(popup.features)) {
    return undefined;
  }

  return popup as ViewPopupLike;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}

function normalizeFeatureIndex(index: number, length: number): number {
  if (!Number.isFinite(index)) {
    return -1;
  }
  const normalized = Math.trunc(index);
  if (normalized < 0 || normalized >= length) {
    return -1;
  }
  return normalized;
}
