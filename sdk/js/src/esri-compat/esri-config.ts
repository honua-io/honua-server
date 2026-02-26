import {
  createEsriRequestInterceptors,
  type EsriRequestInterceptorCompat,
} from "./request.js";

export interface EsriConfigRequestCompat {
  timeout: number;
  useIdentity: boolean;
  trustedServers: string[];
  interceptors: EsriRequestInterceptorCompat[];
}

export interface EsriConfigCompat {
  apiKey: string | undefined;
  portalUrl: string;
  request: EsriConfigRequestCompat;
}

const DEFAULT_PORTAL_URL = "https://www.arcgis.com";

export const esriConfig: EsriConfigCompat = {
  apiKey: undefined,
  portalUrl: DEFAULT_PORTAL_URL,
  request: {
    timeout: 62_000,
    useIdentity: true,
    trustedServers: [],
    interceptors: [],
  },
};

export function resetEsriConfig(): void {
  esriConfig.apiKey = undefined;
  esriConfig.portalUrl = DEFAULT_PORTAL_URL;
  esriConfig.request.timeout = 62_000;
  esriConfig.request.useIdentity = true;
  esriConfig.request.trustedServers.length = 0;
  esriConfig.request.interceptors.length = 0;
}

export function getEsriConfigHonuaInterceptors() {
  return createEsriRequestInterceptors(esriConfig.request.interceptors);
}
