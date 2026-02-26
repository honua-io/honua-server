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
  eventBus?: CompatEventBus;
}

export class FeatureTemplatesCompat {
  public readonly view: unknown;
  public readonly layerInfos: readonly unknown[];
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly filterFunction: ((item: FeatureTemplateItemCompat) => boolean) | undefined;
  public templates: FeatureTemplateItemCompat[];
  public selectedTemplate: FeatureTemplateItemCompat | undefined;

  public constructor(options: FeatureTemplatesCompatOptions = {}) {
    this.view = options.view;
    this.layerInfos = options.layerInfos ? [...options.layerInfos] : [];
    this.container = options.container;
    this.filterFunction = options.filterFunction;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.templates = [];
    this.selectedTemplate = undefined;
  }

  public setTemplates(templates: readonly FeatureTemplateItemCompat[]): void {
    const filtered =
      this.filterFunction === undefined
        ? templates
        : templates.filter((item) => this.filterFunction?.(item) ?? true);
    this.templates = filtered.map((item) => ({ ...item }));
    this.eventBus.emit("feature-templates.updated", { count: this.templates.length }, this);
  }

  public selectTemplate(templateId: string): FeatureTemplateItemCompat | undefined {
    const found = this.templates.find((template) => template.id === templateId);
    this.selectedTemplate = found ? { ...found } : undefined;
    this.eventBus.emit("feature-templates.selected", { template: this.selectedTemplate }, this);
    return this.selectedTemplate;
  }
}
