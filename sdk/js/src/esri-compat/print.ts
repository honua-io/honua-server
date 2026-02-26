import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface PrintTemplateOptionsCompat {
  title?: string;
  format?: "pdf" | "png32" | "jpg";
  layout?: string;
  dpi?: number;
}

export interface PrintCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  printServiceUrl?: string;
  templateOptions?: PrintTemplateOptionsCompat;
}

export interface PrintExecuteOptionsCompat extends PrintTemplateOptionsCompat {}

export interface PrintResultCompat {
  url: string;
  title: string;
  format: string;
  layout: string;
  dpi: number;
}

export class PrintCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public printServiceUrl: string;
  public templateOptions: PrintTemplateOptionsCompat;

  public constructor(options: PrintCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.printServiceUrl = options.printServiceUrl ?? "https://example.test/print";
    this.templateOptions = { ...(options.templateOptions ?? {}) };
  }

  public setTemplateOptions(nextOptions: PrintTemplateOptionsCompat): void {
    this.templateOptions = {
      ...this.templateOptions,
      ...nextOptions,
    };
    this.eventBus.emit("print.template-updated", { templateOptions: { ...this.templateOptions } }, this);
  }

  public async execute(options: PrintExecuteOptionsCompat = {}): Promise<PrintResultCompat> {
    const merged = {
      ...this.templateOptions,
      ...options,
    };
    const title = merged.title ?? "Map Export";
    const format = merged.format ?? "pdf";
    const layout = merged.layout ?? "a4-landscape";
    const dpi = merged.dpi ?? 96;
    this.eventBus.emit("print.execute-started", { title, format, layout, dpi }, this);

    const query = new URLSearchParams();
    query.set("title", title);
    query.set("format", format);
    query.set("layout", layout);
    query.set("dpi", String(dpi));
    const separator = this.printServiceUrl.includes("?") ? "&" : "?";
    const result: PrintResultCompat = {
      url: `${this.printServiceUrl}${separator}${query.toString()}`,
      title,
      format,
      layout,
      dpi,
    };
    this.eventBus.emit("print.execute-completed", result, this);
    return result;
  }
}
