import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";

export interface PopupTemplateCompatOptions {
  title?: unknown;
  content?: unknown;
  fieldInfos?: readonly unknown[];
  actions?: readonly unknown[];
  expressionInfos?: readonly unknown[];
  outFields?: readonly string[];
  eventBus?: CompatEventBus;
}

export type PopupTemplateLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface PopupTemplateHandleCompat {
  remove(): void;
}

export class PopupTemplateCompat {
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: PopupTemplateLoadStatusCompat;
  public title: unknown;
  public content: unknown;
  public fieldInfos: unknown[];
  public actions: unknown[];
  public expressionInfos: unknown[];
  public outFields: string[];
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: PopupTemplateCompatOptions = {}) {
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.fieldInfos, options.actions, options.expressionInfos) ??
      new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.title = options.title;
    this.content = options.content;
    this.fieldInfos = options.fieldInfos ? [...options.fieldInfos] : [];
    this.actions = options.actions ? [...options.actions] : [];
    this.expressionInfos = options.expressionInfos ? [...options.expressionInfos] : [];
    this.outFields = options.outFields ? [...options.outFields] : [];
    this.watchListeners = new Map();
  }

  public async load(): Promise<PopupTemplateCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("popup-template.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("popup-template.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (template: PopupTemplateCompat) => void): Promise<PopupTemplateCompat> {
    const template = await this.load();
    if (callback) {
      callback(template);
    }
    return template;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): PopupTemplateHandleCompat {
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

  public update(options: PopupTemplateCompatOptions): void {
    if (options.title !== undefined) {
      this.title = options.title;
      this.notifyWatchers("title", this.title);
    }
    if (options.content !== undefined) {
      this.content = options.content;
      this.notifyWatchers("content", this.content);
    }
    if (options.fieldInfos !== undefined) {
      this.fieldInfos = [...options.fieldInfos];
      this.notifyWatchers("fieldInfos", this.fieldInfos);
    }
    if (options.actions !== undefined) {
      this.actions = [...options.actions];
      this.notifyWatchers("actions", this.actions);
    }
    if (options.expressionInfos !== undefined) {
      this.expressionInfos = [...options.expressionInfos];
      this.notifyWatchers("expressionInfos", this.expressionInfos);
    }
    if (options.outFields !== undefined) {
      this.outFields = [...options.outFields];
      this.notifyWatchers("outFields", this.outFields);
    }

    this.eventBus.emit("popup-template.updated", undefined, this);
  }

  public getTitle(feature: Record<string, unknown>): string {
    if (typeof this.title !== "string") return String(this.title ?? "");
    return interpolateFieldTokens(this.title, feature);
  }

  public getContent(feature: Record<string, unknown>): string {
    if (typeof this.content !== "string") return String(this.content ?? "");
    return interpolateFieldTokens(this.content, feature);
  }

  public clone(): PopupTemplateCompat {
    return new PopupTemplateCompat({
      title: this.title,
      content: this.content,
      fieldInfos: this.fieldInfos,
      actions: this.actions,
      expressionInfos: this.expressionInfos,
      outFields: this.outFields,
      eventBus: this.eventBus,
    });
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

function interpolateFieldTokens(template: string, attributes: Record<string, unknown>): string {
  return template.replace(/\{([^}]+)\}/g, (_match, fieldName: string) => {
    const value = attributes[fieldName];
    return value === undefined || value === null ? "" : String(value);
  });
}
