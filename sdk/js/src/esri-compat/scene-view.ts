import {
  MapViewCompat,
  type MapViewHandle,
  type MapViewCompatOptions,
  type MapViewGoToInput,
  type MapViewGoToOptions,
  type MapViewLoadStatusCompat,
} from "./map-view.js";

export interface SceneViewCompatOptions extends MapViewCompatOptions {
  viewingMode?: "global" | "local";
  qualityProfile?: string;
  camera?: unknown;
}

export type SceneViewLoadStatusCompat = MapViewLoadStatusCompat;
export type SceneViewHandleCompat = MapViewHandle;

export class SceneViewCompat extends MapViewCompat {
  public viewingMode: "global" | "local";
  public qualityProfile: string | undefined;
  public camera: unknown;

  public constructor(options: SceneViewCompatOptions = {}) {
    super(options);
    this.viewingMode = options.viewingMode ?? "global";
    this.qualityProfile = options.qualityProfile;
    this.camera = options.camera;
  }

  public async load(): Promise<SceneViewCompat> {
    await super.load();
    return this;
  }

  public async when(callback?: (view: SceneViewCompat) => void): Promise<SceneViewCompat> {
    const view = await this.load();
    if (callback) {
      callback(view);
    }
    return view;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): SceneViewHandleCompat {
    return super.watch(propertyName, listener);
  }

  public setViewingMode(viewingMode: "global" | "local"): void {
    this.viewingMode = viewingMode;
    this.notifyWatchers("viewingMode", this.viewingMode);
    this.eventBus.emit("scene-view.viewing-mode-changed", { viewingMode }, this);
  }

  public setQualityProfile(qualityProfile: string | undefined): void {
    this.qualityProfile = qualityProfile;
    this.notifyWatchers("qualityProfile", this.qualityProfile);
    this.eventBus.emit("scene-view.quality-profile-changed", { qualityProfile }, this);
  }

  public setCamera(camera: unknown): void {
    this.camera = camera;
    this.notifyWatchers("camera", this.camera);
    this.eventBus.emit("scene-view.camera-changed", { camera }, this);
  }

  public async goTo(target: MapViewGoToInput, options: MapViewGoToOptions = {}): Promise<SceneViewCompat> {
    const camera = extractCameraTarget(target);
    if (camera !== undefined) {
      this.setCamera(camera);
    }

    await super.goTo(target, options);
    return this;
  }
}

function extractCameraTarget(target: unknown, visited: Set<object> = new Set()): unknown {
  if (typeof target !== "object" || target === null) {
    return undefined;
  }

  if (visited.has(target)) {
    return undefined;
  }
  visited.add(target);

  if ("camera" in target && target.camera !== undefined) {
    return target.camera;
  }

  if ("target" in target && target.target !== undefined) {
    return extractCameraTarget(target.target, visited);
  }

  if ("position" in target || "heading" in target || "tilt" in target) {
    return target;
  }

  return undefined;
}
