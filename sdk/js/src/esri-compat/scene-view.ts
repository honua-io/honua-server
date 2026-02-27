import {
  MapViewCompat,
  type MapViewCompatOptions,
  type MapViewGoToInput,
  type MapViewGoToOptions,
} from "./map-view.js";

export interface SceneViewCompatOptions extends MapViewCompatOptions {
  viewingMode?: "global" | "local";
  qualityProfile?: string;
  camera?: unknown;
}

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

  public setCamera(camera: unknown): void {
    this.camera = camera;
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
