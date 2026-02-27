import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface FeatureTemplateItemCompat {
  id: string;
  name: string;
  description?: string;
}

export interface FeatureTemplatesCompatOptions {
  view?: unknown;
  layerInfos?: readonly unknown[];
  container?: unknown;
  filterFunction?: ((item: FeatureTemplateItemCompat) => boolean) | undefined;
  groupBy?: unknown;
  eventBus?: CompatEventBus;
}

export type FeatureTemplatesLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface FeatureTemplatesHandleCompat {
  remove(): void;
}

export class FeatureTemplatesCompat {
  public readonly view: unknown;
  public readonly layerInfos: readonly unknown[];
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly filterFunction: ((item: FeatureTemplateItemCompat) => boolean) | undefined;
  public readonly groupBy: unknown;
  public loaded: boolean;
  public loadStatus: FeatureTemplatesLoadStatusCompat;
  public templates: FeatureTemplateItemCompat[];
  public selectedTemplate: FeatureTemplateItemCompat | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: FeatureTemplatesCompatOptions = {}) {
    this.view = options.view;
    this.layerInfos = options.layerInfos ? [...options.layerInfos] : [];
    this.container = options.container;
    this.filterFunction = options.filterFunction;
    this.groupBy = options.groupBy;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.templates = [];
    this.selectedTemplate = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<FeatureTemplatesCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-templates.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("feature-templates.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: FeatureTemplatesCompat) => void): Promise<FeatureTemplatesCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(
    propertyName: string,
    listener: (value: unknown) => void,
  ): FeatureTemplatesHandleCompat {
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

  public setTemplates(templates: readonly FeatureTemplateItemCompat[]): void {
    const filtered =
      this.filterFunction === undefined
        ? templates
        : templates.filter((item) => this.filterFunction?.(item) ?? true);
    this.templates = filtered.map((item) => ({ ...item }));
    this.notifyWatchers("templates", this.templates);
    this.eventBus.emit("feature-templates.updated", { count: this.templates.length }, this);
  }

  public selectTemplate(templateId: string): FeatureTemplateItemCompat | undefined {
    const found = this.templates.find((template) => template.id === templateId);
    this.selectedTemplate = found ? { ...found } : undefined;
    this.notifyWatchers("selectedTemplate", this.selectedTemplate);
    this.eventBus.emit("feature-templates.selected", { template: this.selectedTemplate }, this);
    return this.selectedTemplate;
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
      listener(value);
    }
  }
}
