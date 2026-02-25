import { MapViewCompat, type MapViewCompatOptions } from "./map-view.js";

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
}
