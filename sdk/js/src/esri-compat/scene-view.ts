import {
  type MapViewCenterLike,
  MapViewCompat,
  type MapViewCompatOptions,
  type MapViewConstraintsLike,
  type MapViewExtentLike,
  type MapViewGoToInput,
  type MapViewGoToOptions,
  type MapViewHandle,
  type MapViewHighlightOptionsLike,
  type MapViewLayerViewCompat,
  type MapViewLoadStatusCompat,
  type MapViewMapPoint,
  type MapViewPaddingLike,
  type MapViewScreenPoint,
  type MapViewSpatialReferenceLike,
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

  public watch(propertyName: "zoom", listener: (value: number | undefined) => void): SceneViewHandleCompat;
  public watch(propertyName: "scale", listener: (value: number | undefined) => void): SceneViewHandleCompat;
  public watch(propertyName: "rotation", listener: (value: number | undefined) => void): SceneViewHandleCompat;
  public watch(propertyName: "center", listener: (value: MapViewCenterLike | undefined) => void): SceneViewHandleCompat;
  public watch(propertyName: "extent", listener: (value: MapViewExtentLike | undefined) => void): SceneViewHandleCompat;
  public watch(propertyName: "loaded", listener: (value: boolean) => void): SceneViewHandleCompat;
  public watch(propertyName: "loadStatus", listener: (value: MapViewLoadStatusCompat) => void): SceneViewHandleCompat;
  public watch(
    propertyName: "container",
    listener: (value: HTMLElement | string | null | undefined) => void,
  ): SceneViewHandleCompat;
  public watch(
    propertyName: "padding",
    listener: (value: MapViewPaddingLike | undefined) => void,
  ): SceneViewHandleCompat;
  public watch(
    propertyName: "constraints",
    listener: (value: MapViewConstraintsLike | undefined) => void,
  ): SceneViewHandleCompat;
  public watch(
    propertyName: "highlightOptions",
    listener: (value: MapViewHighlightOptionsLike | undefined) => void,
  ): SceneViewHandleCompat;
  public watch(
    propertyName: "spatialReference",
    listener: (value: MapViewSpatialReferenceLike | undefined) => void,
  ): SceneViewHandleCompat;
  public watch(propertyName: "popup.visible", listener: (value: boolean) => void): SceneViewHandleCompat;
  public watch(propertyName: "popup.viewModel.active", listener: (value: boolean) => void): SceneViewHandleCompat;
  public watch(propertyName: "popup.selectedFeatureIndex", listener: (value: number) => void): SceneViewHandleCompat;
  public watch(propertyName: "viewingMode", listener: (value: "global" | "local") => void): SceneViewHandleCompat;
  public watch(propertyName: "camera", listener: (value: unknown) => void): SceneViewHandleCompat;
  public watch(propertyName: string, listener: (value: unknown) => void): SceneViewHandleCompat;
  public watch(propertyName: string, listener: (value: any) => void): SceneViewHandleCompat {
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
