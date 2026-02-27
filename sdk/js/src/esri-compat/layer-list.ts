import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface LayerListCompatOptions {
  view?: unknown;
  map?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  includeHidden?: boolean;
  autoRefresh?: boolean;
  listItemCreatedFunction?: (event: LayerListListItemCreatedEventCompat) => void;
}

export interface LayerListActionCompat {
  id: string;
  title?: string;
  className?: string;
  icon?: string;
  value?: unknown;
}

export interface LayerListItemCompat {
  id: string | undefined;
  title: string;
  visible: boolean;
  layer: unknown;
  actionsSections: LayerListActionCompat[][];
  children: LayerListItemCompat[];
}

export interface LayerListListItemCreatedEventCompat {
  item: LayerListItemCompat;
}

export interface LayerListTriggerActionEventCompat {
  action: LayerListActionCompat;
  actionId: string;
  item: LayerListItemCompat;
  layer: unknown;
}

export interface LayerListUpdatedEventCompat {
  itemCount: number;
}

export interface LayerListHandleCompat {
  remove(): void;
}

interface LayerListEventPayloadByType {
  "trigger-action": LayerListTriggerActionEventCompat;
  updated: LayerListUpdatedEventCompat;
}

type LayerListEventTypeCompat = keyof LayerListEventPayloadByType;
type LayerListListenerCompat = (event: any) => void;

export class LayerListCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly includeHidden: boolean;
  public items: LayerListItemCompat[];

  private readonly autoRefresh: boolean;
  private readonly listItemCreatedFunction: ((event: LayerListListItemCreatedEventCompat) => void) | undefined;
  private readonly listenersByType: Map<LayerListEventTypeCompat, Set<LayerListListenerCompat>>;
  private subscriptions: CompatEventSubscription[];

  public constructor(options: LayerListCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractMapFromView(options.view);
    this.container = options.container;
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(this.view, this.map) ?? new CompatEventBus();
    this.includeHidden = options.includeHidden ?? false;
    this.autoRefresh = options.autoRefresh ?? true;
    this.listItemCreatedFunction = options.listItemCreatedFunction;
    this.items = [];
    this.listenersByType = new Map();
    this.subscriptions = [];

    if (this.autoRefresh) {
      this.subscriptions.push(this.eventBus.on("map.layer-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-removed", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layers-cleared", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("map.layer-reordered", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layer-removed", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-added", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("group-layer.layers-cleared", () => this.refresh()));
      this.subscriptions.push(this.eventBus.on("layer.visibility-changed", () => this.refresh()));
    }
  }

  public async load(): Promise<LayerListCompat> {
    this.refresh();
    return this;
  }

  public async when(callback?: (widget: LayerListCompat) => void): Promise<LayerListCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public refresh(): readonly LayerListItemCompat[] {
    const rootLayers = getRootLayers(this.map);
    this.items = rootLayers
      .map((layer, index) => toLayerListItem(layer, this.includeHidden, index))
      .filter((item): item is LayerListItemCompat => item !== undefined);
    if (this.listItemCreatedFunction) {
      for (const item of this.items) {
        applyListItemCreated(item, this.listItemCreatedFunction);
      }
    }

    const updateEvent: LayerListUpdatedEventCompat = { itemCount: this.items.length };
    this.eventBus.emit("layer-list.updated", updateEvent, this);
    this.emit("updated", updateEvent);
    return this.items;
  }

  public toggle(layerOrId: unknown | string, visible?: boolean): boolean {
    const layer = this.findLayer(layerOrId);
    if (!layer || !isLayerLike(layer)) {
      return false;
    }

    const nextVisible = visible ?? !toVisible(layer);
    layer.visible = nextVisible;
    this.eventBus.emit("layer.visibility-changed", { layerId: layer.id, visible: nextVisible }, this);
    this.refresh();
    return true;
  }

  public setItemActions(
    layerOrId: unknown | string,
    actionsSections: readonly (readonly LayerListActionCompat[])[],
  ): boolean {
    const item = this.findItem(layerOrId);
    if (!item) {
      return false;
    }

    item.actionsSections = normalizeActionSections(actionsSections);
    const updateEvent: LayerListUpdatedEventCompat = { itemCount: this.items.length };
    this.eventBus.emit("layer-list.updated", updateEvent, this);
    this.emit("updated", updateEvent);
    return true;
  }

  public triggerAction(actionId: string, layerOrId?: unknown | string): boolean {
    const item =
      layerOrId === undefined ? this.items[0] : this.findItem(layerOrId);
    if (!item) {
      return false;
    }

    const action = findActionById(item.actionsSections, actionId);
    if (!action) {
      return false;
    }

    const event: LayerListTriggerActionEventCompat = {
      action,
      actionId: action.id,
      item,
      layer: item.layer,
    };
    this.eventBus.emit("layer-list.trigger-action", event, this);
    this.emit("trigger-action", event);
    return true;
  }

  public on<TType extends LayerListEventTypeCompat>(
    type: TType,
    listener: (event: LayerListEventPayloadByType[TType]) => void,
  ): LayerListHandleCompat {
    let listeners = this.listenersByType.get(type);
    if (!listeners) {
      listeners = new Set();
      this.listenersByType.set(type, listeners);
    }

    const untypedListener = listener as LayerListListenerCompat;
    listeners.add(untypedListener);
    return {
      remove: () => {
        listeners?.delete(untypedListener);
      },
    };
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
    this.listenersByType.clear();
  }

  private findLayer(layerOrId: unknown | string): unknown {
    const rootLayers = getRootLayers(this.map);
    if (typeof layerOrId !== "string") {
      return findLayerByReference(rootLayers, layerOrId);
    }
    return findLayerById(rootLayers, layerOrId);
  }

  private findItem(layerOrId: unknown | string): LayerListItemCompat | undefined {
    if (typeof layerOrId !== "string") {
      return findItemByLayer(this.items, layerOrId);
    }
    return findItemById(this.items, layerOrId);
  }

  private emit(type: LayerListEventTypeCompat, event: unknown): void {
    const listeners = this.listenersByType.get(type);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      try {
        listener(event);
      } catch {
        // Listener errors should not break widget compatibility flow.
      }
    }
  }
}

function extractMapFromView(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function getRootLayers(map: unknown): unknown[] {
  if (!isRecord(map) || !Array.isArray(map.layers)) {
    return [];
  }
  return [...map.layers];
}

function toLayerListItem(
  layer: unknown,
  includeHidden: boolean,
  index: number,
): LayerListItemCompat | undefined {
  if (!isLayerLike(layer)) {
    return undefined;
  }

  if (!includeHidden && !toVisible(layer)) {
    return undefined;
  }

  const children = getChildLayers(layer)
    .map((child, childIndex) => toLayerListItem(child, includeHidden, childIndex))
    .filter((item): item is LayerListItemCompat => item !== undefined);

  return {
    id: layer.id,
    title: toLayerTitle(layer, index),
    visible: toVisible(layer),
    layer,
    actionsSections: [],
    children,
  };
}

function applyListItemCreated(
  item: LayerListItemCompat,
  callback: (event: LayerListListItemCreatedEventCompat) => void,
): void {
  callback({ item });
  for (const child of item.children) {
    applyListItemCreated(child, callback);
  }
}

function normalizeActionSections(
  actionsSections: readonly (readonly LayerListActionCompat[])[],
): LayerListActionCompat[][] {
  return actionsSections.map((section) =>
    section
      .filter((action) => typeof action.id === "string" && action.id.trim().length > 0)
      .map((action) => ({ ...action })),
  );
}

function findActionById(
  actionsSections: readonly (readonly LayerListActionCompat[])[],
  actionId: string,
): LayerListActionCompat | undefined {
  for (const section of actionsSections) {
    for (const action of section) {
      if (action.id === actionId) {
        return action;
      }
    }
  }
  return undefined;
}

function findItemByLayer(
  items: readonly LayerListItemCompat[],
  layer: unknown,
): LayerListItemCompat | undefined {
  for (const item of items) {
    if (item.layer === layer) {
      return item;
    }
    const nested = findItemByLayer(item.children, layer);
    if (nested) {
      return nested;
    }
  }
  return undefined;
}

function findItemById(
  items: readonly LayerListItemCompat[],
  id: string,
): LayerListItemCompat | undefined {
  for (const item of items) {
    if (item.id === id) {
      return item;
    }
    const nested = findItemById(item.children, id);
    if (nested) {
      return nested;
    }
  }
  return undefined;
}

function findLayerByReference(layers: readonly unknown[], target: unknown): unknown {
  for (const layer of layers) {
    if (layer === target) {
      return layer;
    }
    const nested = findLayerByReference(getChildLayers(layer), target);
    if (nested !== undefined) {
      return nested;
    }
  }
  return undefined;
}

function findLayerById(layers: readonly unknown[], id: string): unknown {
  for (const layer of layers) {
    if (isLayerLike(layer) && layer.id === id) {
      return layer;
    }
    const nested = findLayerById(getChildLayers(layer), id);
    if (nested !== undefined) {
      return nested;
    }
  }
  return undefined;
}

interface LayerLike {
  id: string | undefined;
  title: string | undefined;
  visible: boolean | undefined;
  layers?: unknown[];
}

function isLayerLike(value: unknown): value is LayerLike {
  return typeof value === "object" && value !== null;
}

function getChildLayers(layer: unknown): unknown[] {
  if (!isRecord(layer) || !Array.isArray(layer.layers)) {
    return [];
  }
  return [...layer.layers];
}

function toLayerTitle(layer: LayerLike, index: number): string {
  if (typeof layer.title === "string" && layer.title.trim().length > 0) {
    return layer.title;
  }
  if (typeof layer.id === "string" && layer.id.trim().length > 0) {
    return layer.id;
  }
  return `Layer ${index + 1}`;
}

function toVisible(layer: LayerLike): boolean {
  return layer.visible ?? true;
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
