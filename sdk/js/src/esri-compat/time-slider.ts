import {
  CompatEventBus,
  type CompatEventSubscription,
  resolveCompatEventBus,
  safeInvokeCompatListener,
} from "./event-bus.js";

export type TimeSliderModeCompat = "instant" | "time-window";
export type TimeSliderIntervalUnitCompat = "milliseconds" | "seconds" | "minutes" | "hours" | "days";

export interface TimeExtentCompat {
  start: Date;
  end: Date;
}

export interface TimeSliderStopsCompat {
  values?: readonly (Date | string | number)[];
  interval?: {
    value: number;
    unit: TimeSliderIntervalUnitCompat;
  };
}

export interface TimeSliderCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  fullTimeExtent?: Partial<{
    start: Date | string | number;
    end: Date | string | number;
  }>;
  timeExtent?: Partial<{
    start: Date | string | number;
    end: Date | string | number;
  }>;
  stops?: TimeSliderStopsCompat;
  mode?: TimeSliderModeCompat;
  loop?: boolean;
  playRate?: number;
}

export type TimeSliderLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface TimeSliderHandleCompat {
  remove(): void;
}

export class TimeSliderCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: TimeSliderLoadStatusCompat;
  public readonly mode: TimeSliderModeCompat;
  public readonly loop: boolean;
  public readonly playRate: number;
  public readonly stops: TimeSliderStopsCompat;
  public fullTimeExtent: TimeExtentCompat | undefined;
  public timeExtent: TimeExtentCompat | undefined;
  public playing: boolean;

  private stopIndex: number;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: TimeSliderCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.mode = options.mode ?? "instant";
    this.loop = options.loop ?? false;
    this.playRate = options.playRate ?? 1000;
    this.stops = {
      values: options.stops?.values ? [...options.stops.values] : undefined,
      interval: options.stops?.interval ? { ...options.stops.interval } : undefined,
    };
    this.fullTimeExtent = parseTimeExtent(options.fullTimeExtent);
    this.timeExtent = parseTimeExtent(options.timeExtent) ?? this.fullTimeExtent;
    this.playing = false;
    this.stopIndex = 0;
    this.watchListeners = new Map();
  }

  public async load(): Promise<TimeSliderCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("timeslider.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("timeslider.loaded", undefined, this);
    return this;
  }

  public async when(callback?: (widget: TimeSliderCompat) => void): Promise<TimeSliderCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): TimeSliderHandleCompat {
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

  public setTimeExtent(timeExtent: TimeExtentCompat): void {
    this.timeExtent = {
      start: new Date(timeExtent.start.getTime()),
      end: new Date(timeExtent.end.getTime()),
    };
    this.notifyWatchers("timeExtent", this.timeExtent);
    this.eventBus.emit("timeslider.updated", { timeExtent: this.timeExtent }, this);
  }

  public play(): void {
    if (this.playing) {
      return;
    }
    this.playing = true;
    this.notifyWatchers("playing", this.playing);
    this.eventBus.emit("timeslider.play", { playRate: this.playRate }, this);
  }

  public stop(): void {
    if (!this.playing) {
      return;
    }
    this.playing = false;
    this.notifyWatchers("playing", this.playing);
    this.eventBus.emit("timeslider.stop", undefined, this);
  }

  public next(): TimeExtentCompat | undefined {
    const nextExtent = this.shift(1);
    if (nextExtent) {
      this.eventBus.emit("timeslider.next", { timeExtent: nextExtent }, this);
    }
    return nextExtent;
  }

  public previous(): TimeExtentCompat | undefined {
    const previousExtent = this.shift(-1);
    if (previousExtent) {
      this.eventBus.emit("timeslider.previous", { timeExtent: previousExtent }, this);
    }
    return previousExtent;
  }

  private shift(stepDirection: 1 | -1): TimeExtentCompat | undefined {
    const stopValues = this.stops.values?.map((value) => toDate(value)).filter((value): value is Date => !!value);
    if (stopValues && stopValues.length > 0) {
      const lastIndex = stopValues.length - 1;
      this.stopIndex += stepDirection;

      if (this.stopIndex < 0 || this.stopIndex > lastIndex) {
        if (!this.loop) {
          this.stopIndex = Math.max(0, Math.min(lastIndex, this.stopIndex));
          return this.timeExtent;
        }
        this.stopIndex = this.stopIndex < 0 ? lastIndex : 0;
      }

      const current = stopValues[this.stopIndex];
      if (!current) {
        return this.timeExtent;
      }
      const extent = {
        start: current,
        end: current,
      };
      this.setTimeExtent(extent);
      return this.timeExtent;
    }

    const interval = this.stops.interval;
    if (!interval || !this.timeExtent) {
      return this.timeExtent;
    }

    const deltaMillis = intervalToMilliseconds(interval.value, interval.unit) * stepDirection;
    const shifted: TimeExtentCompat = {
      start: new Date(this.timeExtent.start.getTime() + deltaMillis),
      end: new Date(this.timeExtent.end.getTime() + deltaMillis),
    };
    this.setTimeExtent(shifted);
    return this.timeExtent;
  }

  public connectLayer(layer: {
    setTimeExtent(extent: { start: Date; end: Date } | undefined): void;
  }): TimeSliderHandleCompat {
    if (this.timeExtent) {
      layer.setTimeExtent(this.timeExtent);
    }

    const subscription: CompatEventSubscription = this.eventBus.on("timeslider.updated", (event) => {
      const payload = event.payload as { timeExtent?: { start: Date; end: Date } } | undefined;
      if (payload?.timeExtent) {
        layer.setTimeExtent(payload.timeExtent);
      }
    });

    return {
      remove: () => {
        subscription.remove();
      },
    };
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

function parseTimeExtent(
  extent:
    | Partial<{
        start: Date | string | number;
        end: Date | string | number;
      }>
    | undefined,
): TimeExtentCompat | undefined {
  if (!extent) {
    return undefined;
  }

  const start = toDate(extent.start);
  const end = toDate(extent.end);
  if (!start || !end) {
    return undefined;
  }
  return { start, end };
}

function toDate(value: Date | string | number | undefined): Date | undefined {
  if (value instanceof Date) {
    return new Date(value.getTime());
  }
  if (typeof value === "string" || typeof value === "number") {
    const parsed = new Date(value);
    if (!Number.isNaN(parsed.getTime())) {
      return parsed;
    }
  }
  return undefined;
}

function intervalToMilliseconds(value: number, unit: TimeSliderIntervalUnitCompat): number {
  switch (unit) {
    case "milliseconds":
      return value;
    case "seconds":
      return value * 1000;
    case "minutes":
      return value * 60_000;
    case "hours":
      return value * 3_600_000;
    case "days":
      return value * 86_400_000;
  }
}
