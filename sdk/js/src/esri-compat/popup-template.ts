import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface PopupTemplateCompatOptions {
  title?: unknown;
  content?: unknown;
  fieldInfos?: readonly unknown[];
  actions?: readonly unknown[];
  expressionInfos?: readonly unknown[];
  outFields?: readonly string[];
  eventBus?: CompatEventBus;
}

export class PopupTemplateCompat {
  public readonly eventBus: CompatEventBus;
  public title: unknown;
  public content: unknown;
  public fieldInfos: unknown[];
  public actions: unknown[];
  public expressionInfos: unknown[];
  public outFields: string[];

  public constructor(options: PopupTemplateCompatOptions = {}) {
    this.eventBus =
      options.eventBus ??
      resolveCompatEventBus(options.fieldInfos, options.actions, options.expressionInfos) ??
      new CompatEventBus();
    this.title = options.title;
    this.content = options.content;
    this.fieldInfos = options.fieldInfos ? [...options.fieldInfos] : [];
    this.actions = options.actions ? [...options.actions] : [];
    this.expressionInfos = options.expressionInfos ? [...options.expressionInfos] : [];
    this.outFields = options.outFields ? [...options.outFields] : [];
  }

  public update(options: PopupTemplateCompatOptions): void {
    if (options.title !== undefined) {
      this.title = options.title;
    }
    if (options.content !== undefined) {
      this.content = options.content;
    }
    if (options.fieldInfos !== undefined) {
      this.fieldInfos = [...options.fieldInfos];
    }
    if (options.actions !== undefined) {
      this.actions = [...options.actions];
    }
    if (options.expressionInfos !== undefined) {
      this.expressionInfos = [...options.expressionInfos];
    }
    if (options.outFields !== undefined) {
      this.outFields = [...options.outFields];
    }

    this.eventBus.emit("popup-template.updated", undefined, this);
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
}
