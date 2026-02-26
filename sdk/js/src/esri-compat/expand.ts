import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface ExpandCompatOptions {
  view?: unknown;
  container?: unknown;
  content?: unknown;
  expanded?: boolean;
  mode?: "auto" | "floating" | "drawer";
  group?: string;
  eventBus?: CompatEventBus;
}

export class ExpandCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public content: unknown;
  public expanded: boolean;
  public mode: "auto" | "floating" | "drawer";
  public group: string | undefined;

  public constructor(options: ExpandCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.content = options.content;
    this.expanded = options.expanded ?? false;
    this.mode = options.mode ?? "auto";
    this.group = options.group;
  }

  public expand(): void {
    if (this.expanded) {
      return;
    }
    this.expanded = true;
    this.eventBus.emit("expand.changed", { expanded: true }, this);
  }

  public collapse(): void {
    if (!this.expanded) {
      return;
    }
    this.expanded = false;
    this.eventBus.emit("expand.changed", { expanded: false }, this);
  }

  public toggle(force?: boolean): boolean {
    const next = force ?? !this.expanded;
    if (next) {
      this.expand();
    } else {
      this.collapse();
    }
    return this.expanded;
  }
}
