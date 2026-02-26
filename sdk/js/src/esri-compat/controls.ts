import { CompatEventBus, type CompatEventSubscription, resolveCompatEventBus } from "./event-bus.js";

export interface HomeCompatOptions {
  view?: unknown;
  eventBus?: CompatEventBus;
  viewpoint?: HomeViewpointCompat;
}

export interface HomeViewpointCompat {
  center?: unknown;
  zoom?: number;
}

export class HomeCompat {
  public readonly view: unknown;
  public readonly eventBus: CompatEventBus;
  public viewpoint: HomeViewpointCompat;

  public constructor(options: HomeCompatOptions = {}) {
    this.view = options.view;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.viewpoint = options.viewpoint ?? {
      center: extractViewCenter(options.view),
      zoom: extractViewZoom(options.view),
    };
  }

  public async go(): Promise<void> {
    const target = {
      center: this.viewpoint.center,
      zoom: this.viewpoint.zoom,
    };

    if (isGoToProvider(this.view)) {
      await this.view.goTo(target);
    } else {
      setViewCenterZoom(this.view, target);
    }

    this.eventBus.emit("home.go", target, this);
  }

  public reset(): void {
    this.viewpoint = {
      center: extractViewCenter(this.view),
      zoom: extractViewZoom(this.view),
    };
    this.eventBus.emit("home.reset", this.viewpoint, this);
  }
}

export interface BasemapToggleCompatOptions {
  view?: unknown;
  map?: unknown;
  nextBasemap?: unknown;
  eventBus?: CompatEventBus;
}

export class BasemapToggleCompat {
  public readonly view: unknown;
  public readonly map: unknown;
  public readonly eventBus: CompatEventBus;
  public activeBasemap: unknown;
  public nextBasemap: unknown;

  public constructor(options: BasemapToggleCompatOptions = {}) {
    this.view = options.view;
    this.map = options.map ?? extractViewMap(options.view);
    this.eventBus =
      options.eventBus ?? resolveCompatEventBus(options.view, options.map) ?? new CompatEventBus();
    this.activeBasemap = extractMapBasemap(this.map);
    this.nextBasemap = options.nextBasemap;
  }

  public toggle(): unknown {
    const currentMap = this.map;
    if (!isRecord(currentMap)) {
      return undefined;
    }

    const previous = currentMap.basemap;
    currentMap.basemap = this.nextBasemap;
    this.activeBasemap = currentMap.basemap;
    this.nextBasemap = previous;
    this.eventBus.emit(
      "basemap.toggle",
      {
        activeBasemap: this.activeBasemap,
        nextBasemap: this.nextBasemap,
      },
      this,
    );
    return this.activeBasemap;
  }
}

export type ScaleBarUnitCompat = "metric" | "imperial" | "dual";

export interface ScaleBarCompatOptions {
  view?: unknown;
  unit?: ScaleBarUnitCompat;
  eventBus?: CompatEventBus;
}

export class ScaleBarCompat {
  public readonly view: unknown;
  public readonly eventBus: CompatEventBus;
  public unit: ScaleBarUnitCompat;
  public scale: number | undefined;
  public text: string;

  private readonly subscriptions: CompatEventSubscription[];

  public constructor(options: ScaleBarCompatOptions = {}) {
    this.view = options.view;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.unit = options.unit ?? "metric";
    this.scale = undefined;
    this.text = "";
    this.subscriptions = [
      this.eventBus.on("view.go-to", () => {
        this.refresh();
      }),
    ];
    this.refresh();
  }

  public refresh(): string {
    const zoom = extractViewZoom(this.view);
    if (zoom === undefined) {
      this.scale = undefined;
      this.text = "";
      return this.text;
    }

    const mapScale = 591657527.591555 / Math.pow(2, zoom);
    this.scale = mapScale;
    this.text = buildScaleBarText(mapScale, this.unit);
    this.eventBus.emit("scalebar.updated", { scale: mapScale, text: this.text, unit: this.unit }, this);
    return this.text;
  }

  public destroy(): void {
    for (const subscription of this.subscriptions.splice(0)) {
      subscription.remove();
    }
  }
}

export interface LocateCompatOptions {
  view?: unknown;
  eventBus?: CompatEventBus;
  zoom?: number;
  locateProvider?: () => Promise<LocatePositionCompat>;
}

export interface LocatePositionCompat {
  coords: {
    latitude: number;
    longitude: number;
    accuracy?: number;
  };
}

export class LocateCompat {
  public readonly view: unknown;
  public readonly eventBus: CompatEventBus;
  public readonly zoom: number | undefined;

  private readonly locateProvider: () => Promise<LocatePositionCompat>;

  public constructor(options: LocateCompatOptions = {}) {
    this.view = options.view;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.zoom = options.zoom;
    this.locateProvider = options.locateProvider ?? getDefaultLocateProvider();
  }

  public async locate(): Promise<LocatePositionCompat> {
    this.eventBus.emit("locate.start", undefined, this);

    try {
      const position = await this.locateProvider();
      const center: [number, number] = [position.coords.longitude, position.coords.latitude];
      const target = {
        center,
        zoom: this.zoom,
      };
      if (isGoToProvider(this.view)) {
        await this.view.goTo(target);
      } else {
        setViewCenterZoom(this.view, target);
      }
      this.eventBus.emit(
        "locate.success",
        {
          position,
          center,
          zoom: this.zoom,
        },
        this,
      );
      return position;
    } catch (error) {
      this.eventBus.emit("locate.error", { error }, this);
      throw error;
    }
  }
}

interface GoToProvider {
  goTo(target: { center?: unknown; zoom?: number }): Promise<unknown> | unknown;
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function setViewCenterZoom(view: unknown, target: { center?: unknown; zoom?: number }): void {
  if (!isRecord(view)) {
    return;
  }
  if (target.center !== undefined) {
    view.center = target.center;
  }
  if (typeof target.zoom === "number") {
    view.zoom = target.zoom;
  }
}

function extractViewCenter(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.center;
}

function extractViewZoom(view: unknown): number | undefined {
  if (!isRecord(view) || typeof view.zoom !== "number" || !Number.isFinite(view.zoom)) {
    return undefined;
  }
  return view.zoom;
}

function extractViewMap(view: unknown): unknown {
  if (!isRecord(view)) {
    return undefined;
  }
  return view.map;
}

function extractMapBasemap(map: unknown): unknown {
  if (!isRecord(map)) {
    return undefined;
  }
  return map.basemap;
}

function buildScaleBarText(scale: number, unit: ScaleBarUnitCompat): string {
  const ratioText = `1:${Math.max(1, Math.round(scale)).toLocaleString("en-US")}`;
  if (unit === "metric") {
    return `${ratioText} | ${formatMetricDistance(scale)}`;
  }
  if (unit === "imperial") {
    return `${ratioText} | ${formatImperialDistance(scale)}`;
  }
  return `${ratioText} | ${formatMetricDistance(scale)} / ${formatImperialDistance(scale)}`;
}

function formatMetricDistance(scale: number): string {
  const meters = Math.max(1, Math.round(scale * 0.00028));
  if (meters >= 1000) {
    return `${Math.round(meters / 1000)} km`;
  }
  return `${meters} m`;
}

function formatImperialDistance(scale: number): string {
  const feet = Math.max(1, Math.round(scale * 0.0009186351706));
  if (feet >= 5280) {
    return `${Math.round(feet / 5280)} mi`;
  }
  return `${feet} ft`;
}

function getDefaultLocateProvider(): () => Promise<LocatePositionCompat> {
  return () =>
    new Promise<LocatePositionCompat>((resolve, reject) => {
      const geolocation = globalThis.navigator?.geolocation;
      if (!geolocation || typeof geolocation.getCurrentPosition !== "function") {
        reject(new Error("Geolocation API is unavailable; provide locateProvider."));
        return;
      }

      geolocation.getCurrentPosition(
        (position) => {
          resolve({
            coords: {
              latitude: position.coords.latitude,
              longitude: position.coords.longitude,
              accuracy: position.coords.accuracy,
            },
          });
        },
        (error) => {
          reject(error);
        },
      );
    });
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
