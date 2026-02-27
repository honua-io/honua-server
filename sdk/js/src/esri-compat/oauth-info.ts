export interface OAuthInfoCompatOptions {
  appId?: string;
  portalUrl?: string;
  popup?: boolean;
  flowType?: "auto" | "implicit" | "authorization-code" | string;
  expiration?: number;
  authNamespace?: string;
  preserveUrlHash?: boolean;
}

export class OAuthInfoCompat {
  public appId: string | undefined;
  public portalUrl: string;
  public popup: boolean;
  public flowType: string;
  public expiration: number | undefined;
  public authNamespace: string | undefined;
  public preserveUrlHash: boolean;

  public constructor(options: OAuthInfoCompatOptions = {}) {
    this.appId = options.appId;
    this.portalUrl = options.portalUrl ?? "https://www.arcgis.com";
    this.popup = options.popup ?? false;
    this.flowType = options.flowType ?? "auto";
    this.expiration =
      typeof options.expiration === "number" && Number.isFinite(options.expiration)
        ? options.expiration
        : undefined;
    this.authNamespace = options.authNamespace;
    this.preserveUrlHash = options.preserveUrlHash ?? false;
  }

  public clone(): OAuthInfoCompat {
    return new OAuthInfoCompat(this.toJSON());
  }

  public toJSON(): OAuthInfoCompatOptions {
    return {
      appId: this.appId,
      portalUrl: this.portalUrl,
      popup: this.popup,
      flowType: this.flowType,
      expiration: this.expiration,
      authNamespace: this.authNamespace,
      preserveUrlHash: this.preserveUrlHash,
    };
  }
}
