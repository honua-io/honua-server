import { OAuthInfoCompat, type OAuthInfoCompatOptions } from "./oauth-info.js";

export interface IdentityCredentialCompat {
  server: string;
  token: string;
  expires?: number;
  userId?: string;
}

export interface IdentityTokenRegistrationCompat {
  server: string;
  token: string;
  expires?: number;
  userId?: string;
}

class IdentityManagerCompatStore {
  private readonly oauthInfosInternal: OAuthInfoCompat[];
  private readonly credentialsInternal: IdentityCredentialCompat[];

  public constructor() {
    this.oauthInfosInternal = [];
    this.credentialsInternal = [];
  }

  public get oauthInfos(): readonly OAuthInfoCompat[] {
    return this.oauthInfosInternal.map((info) => info.clone());
  }

  public get credentials(): readonly IdentityCredentialCompat[] {
    return this.credentialsInternal.map((credential) => ({ ...credential }));
  }

  public registerOAuthInfos(infos: readonly (OAuthInfoCompat | OAuthInfoCompatOptions)[]): void {
    this.oauthInfosInternal.length = 0;
    for (const info of infos) {
      this.oauthInfosInternal.push(info instanceof OAuthInfoCompat ? info.clone() : new OAuthInfoCompat(info));
    }
  }

  public registerToken(token: IdentityTokenRegistrationCompat): void {
    const next: IdentityCredentialCompat = {
      server: token.server,
      token: token.token,
      expires: typeof token.expires === "number" && Number.isFinite(token.expires) ? token.expires : undefined,
      userId: token.userId,
    };

    const existingIndex = this.credentialsInternal.findIndex((item) => item.server === next.server);
    if (existingIndex >= 0) {
      this.credentialsInternal[existingIndex] = next;
      return;
    }
    this.credentialsInternal.push(next);
  }

  public findCredential(url: string): IdentityCredentialCompat | undefined {
    const normalized = normalizeServerUrl(url);
    if (!normalized) {
      return undefined;
    }

    const match = this.credentialsInternal.find((credential) => {
      const server = normalizeServerUrl(credential.server);
      if (!server) {
        return false;
      }
      return normalized === server || normalized.startsWith(`${server}/`);
    });
    return match ? { ...match } : undefined;
  }

  public async checkSignInStatus(url: string): Promise<IdentityCredentialCompat> {
    const credential = this.findCredential(url);
    if (!credential) {
      throw new Error("No registered credential for requested server.");
    }
    return credential;
  }

  public async getCredential(url: string): Promise<IdentityCredentialCompat> {
    return this.checkSignInStatus(url);
  }

  public destroyCredentials(): void {
    this.credentialsInternal.length = 0;
  }

  public reset(): void {
    this.oauthInfosInternal.length = 0;
    this.credentialsInternal.length = 0;
  }
}

export const identityManager = new IdentityManagerCompatStore();

function normalizeServerUrl(value: string): string | undefined {
  try {
    const url = new URL(value);
    const pathname = url.pathname.endsWith("/") ? url.pathname.slice(0, -1) : url.pathname;
    return `${url.protocol}//${url.host}${pathname}`;
  } catch {
    return undefined;
  }
}
