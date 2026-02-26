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

export class PopupCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public visible: boolean;
  public location: unknown;
  public features: unknown[];
  public title: string | undefined;
  public content: unknown;
  public autoOpenEnabled: boolean;
  public dockEnabled: boolean;
  public dockOptions: unknown;
  public selectedFeature: unknown;

  private readonly subscriptions: CompatEventSubscription[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PopupCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.visible = false;
    this.location = undefined;
    this.features = [];
    this.title = undefined;
    this.content = undefined;
    this.autoOpenEnabled = options.autoOpenEnabled ?? true;
    this.dockEnabled = options.dockEnabled ?? false;
    this.dockOptions = options.dockOptions;
    this.selectedFeature = undefined;
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

  public open(options: PopupOpenOptionsCompat = {}): void {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup) {
      viewPopup.open(options);
      this.syncFromViewPopup();
      return;
    }

    this.applyOpenOptions(options);
    this.visible = true;
    this.selectedFeature = this.features[0];
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.eventBus.emit("popup.open", options, this);
  }

  public close(): void {
    const viewPopup = resolveViewPopup(this.view);
    if (viewPopup) {
      viewPopup.close();
      this.syncFromViewPopup();
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
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
    this.eventBus.emit("popup.close", undefined, this);
  }

  public clear(): void {
    this.close();
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
    this.notifyWatchers("visible", this.visible);
    this.notifyWatchers("location", this.location);
    this.notifyWatchers("features", this.features);
    this.notifyWatchers("title", this.title);
    this.notifyWatchers("content", this.content);
    this.notifyWatchers("selectedFeature", this.selectedFeature);
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
  selectedFeature?: unknown;
  title: string | undefined;
  content: unknown;
  open(options?: PopupOpenOptionsCompat): void;
  close(): void;
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
