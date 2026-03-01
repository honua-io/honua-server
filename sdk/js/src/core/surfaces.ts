import type { HonuaClient } from "./client.js";
import type {
  ApplyEditsRequest,
  ExportMapRequest,
  HonuaAddAttachmentResponse,
  HonuaApplyEditsResponse,
  HonuaAttachmentListResponse,
  HonuaDeleteAttachmentsResponse,
  HonuaExtent,
  HonuaExportMapResponse,
  HonuaFeature,
  HonuaFindResponse,
  HonuaIdentifyResponse,
  HonuaLayerMetadata,
  HonuaLegendResponse,
  HonuaOgcCollectionMetadata,
  HonuaOgcCollectionsResponse,
  HonuaOgcConformanceResponse,
  HonuaOgcFeatureCollectionResponse,
  HonuaOgcFeatureResponse,
  HonuaOgcLandingResponse,
  HonuaOgcQueryablesResponse,
  HonuaQueryAttachmentsResponse,
  HonuaQueryResponse,
  HonuaRawRequest,
  HonuaRelatedRecordsResponse,
  HonuaServiceMetadata,
  HonuaTypedFeature,
  HonuaTypedQueryResponse,
  HonuaUpdateAttachmentResponse,
  OgcCollectionRequest,
  OgcCreateItemRequest,
  OgcDeleteItemRequest,
  OgcItemRequest,
  OgcItemsRequest,
  OgcMetadataRequest,
  OgcPatchItemRequest,
  OgcReplaceItemRequest,
  MapLayerQueryRequest,
  MapFindRequest,
  MapIdentifyRequest,
  MapLegendRequest,
  MapRelatedRecordsRequest,
  QueryFeaturesRequest,
  QueryMethod,
  QueryRelatedRecordsRequest,
} from "./types.js";

export interface HonuaServiceOptions {
  client: HonuaClient;
  serviceId: string;
}

export type HonuaServiceRequest = Omit<HonuaRawRequest, "path"> & {
  path: string;
};

export type HonuaFeatureLayerQueryRequest = Omit<QueryFeaturesRequest, "serviceId" | "layerId">;
export type HonuaFeatureLayerQueryAllRequest = HonuaFeatureLayerQueryRequest & {
  pageSize?: number;
  maxPages?: number;
};
export type HonuaFeatureLayerQueryCountRequest = Pick<QueryFeaturesRequest, "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaFeatureLayerQueryObjectIdsRequest = Pick<QueryFeaturesRequest, "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaFeatureLayerQueryExtentRequest = HonuaFeatureLayerQueryCountRequest;
export type HonuaFeatureLayerQueryRelatedRecordsRequest = Omit<
  QueryRelatedRecordsRequest,
  "serviceId" | "layerId"
>;
export type HonuaFeatureLayerApplyEditsRequest = Omit<ApplyEditsRequest, "serviceId" | "layerId">;
export interface HonuaFeatureLayerQueryExtentResponse {
  extent: HonuaExtent | null;
  count?: number;
}
export interface HonuaFeatureLayerQueryAttachmentsRequest {
  objectIds?: readonly number[] | string;
  where?: string;
  method?: QueryMethod;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}
export interface HonuaFeatureLayerListAttachmentsRequest {
  objectId: number | string;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}
export interface HonuaFeatureLayerDeleteAttachmentsRequest {
  objectId: number | string;
  attachmentIds: readonly number[] | string;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}
export type HonuaFeatureLayerAttachmentData = Blob | File | string;
export interface HonuaFeatureLayerAddAttachmentRequest {
  objectId: number | string;
  attachment: HonuaFeatureLayerAttachmentData;
  name?: string;
  contentType?: string;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}
export interface HonuaFeatureLayerUpdateAttachmentRequest extends HonuaFeatureLayerAddAttachmentRequest {
  attachmentId: number | string;
}
export type HonuaFeatureLayerRequest = Omit<HonuaRawRequest, "path"> & {
  path: string;
};
export type HonuaMapServiceExportMapRequest = Omit<ExportMapRequest, "serviceId">;
export type HonuaMapServiceLegendRequest = Omit<MapLegendRequest, "serviceId">;
export type HonuaMapServiceIdentifyRequest = Omit<MapIdentifyRequest, "serviceId">;
export type HonuaMapServiceFindRequest = Omit<MapFindRequest, "serviceId">;
export type HonuaMapServiceRequest = Omit<HonuaRawRequest, "path"> & {
  path: string;
};
export type HonuaMapServiceQueryLayerRequest = Omit<MapLayerQueryRequest, "serviceId">;
export type HonuaMapServiceQueryLayerAllRequest = HonuaMapServiceQueryLayerRequest & {
  pageSize?: number;
  maxPages?: number;
};
export type HonuaMapServiceQueryLayerRelatedRecordsRequest = Omit<
  MapRelatedRecordsRequest,
  "serviceId"
>;
export type HonuaMapServiceQueryLayerCountRequest = Pick<
  MapLayerQueryRequest,
  "layerId" | "where" | "method"
> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaMapServiceQueryLayerObjectIdsRequest = Pick<
  MapLayerQueryRequest,
  "layerId" | "where" | "method"
> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaMapServiceQueryLayerExtentRequest = HonuaMapServiceQueryLayerCountRequest;
export interface HonuaMapServiceQueryLayerExtentResponse {
  extent: HonuaExtent | null;
  count?: number;
}
export type HonuaMapLayerQueryRequest = Omit<MapLayerQueryRequest, "serviceId" | "layerId">;
export type HonuaMapLayerQueryAllRequest = HonuaMapLayerQueryRequest & {
  pageSize?: number;
  maxPages?: number;
};
export type HonuaMapLayerQueryRelatedRecordsRequest = Omit<
  MapRelatedRecordsRequest,
  "serviceId" | "layerId"
>;
export type HonuaMapLayerQueryCountRequest = Pick<MapLayerQueryRequest, "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaMapLayerQueryObjectIdsRequest = Pick<MapLayerQueryRequest, "where" | "method"> & {
  extraParams?: Record<string, string | number | boolean>;
};
export type HonuaMapLayerQueryExtentRequest = HonuaMapLayerQueryCountRequest;
export interface HonuaMapLayerQueryExtentResponse {
  extent: HonuaExtent | null;
  count?: number;
}
export type HonuaMapLayerRequest = Omit<HonuaRawRequest, "path"> & {
  path: string;
};
export type HonuaOgcMetadataRequest = OgcMetadataRequest;
export type HonuaOgcCollectionRequest = OgcCollectionRequest;
export type HonuaOgcItemsRequest = OgcItemsRequest;
export type HonuaOgcItemRequest = OgcItemRequest;
export type HonuaOgcCreateItemRequest = OgcCreateItemRequest;
export type HonuaOgcReplaceItemRequest = OgcReplaceItemRequest;
export type HonuaOgcPatchItemRequest = OgcPatchItemRequest;
export type HonuaOgcDeleteItemRequest = OgcDeleteItemRequest;
export type HonuaOgcCollectionItemsRequest = Omit<OgcItemsRequest, "collectionId">;
export type HonuaOgcItemsAllRequest = HonuaOgcItemsRequest & {
  pageSize?: number;
  maxPages?: number;
};
export type HonuaOgcCollectionItemsAllRequest = HonuaOgcCollectionItemsRequest & {
  pageSize?: number;
  maxPages?: number;
};
export type HonuaOgcCollectionItemRequest = Omit<OgcItemRequest, "collectionId">;
export type HonuaOgcCollectionCreateItemRequest = Omit<OgcCreateItemRequest, "collectionId">;
export type HonuaOgcCollectionReplaceItemRequest = Omit<OgcReplaceItemRequest, "collectionId">;
export type HonuaOgcCollectionPatchItemRequest = Omit<OgcPatchItemRequest, "collectionId">;
export type HonuaOgcCollectionDeleteItemRequest = Omit<OgcDeleteItemRequest, "collectionId">;

export class HonuaService {
  public readonly client: HonuaClient;
  public readonly serviceId: string;

  public constructor(options: HonuaServiceOptions) {
    this.client = options.client;
    this.serviceId = options.serviceId;
  }

  public featureLayer<T = Record<string, unknown>>(layerId: number): HonuaFeatureLayer<T> {
    return new HonuaFeatureLayer<T>({
      client: this.client,
      serviceId: this.serviceId,
      layerId,
    });
  }

  public layer<T = Record<string, unknown>>(layerId: number): HonuaFeatureLayer<T> {
    return this.featureLayer<T>(layerId);
  }

  public async featureServiceMetadata(): Promise<HonuaServiceMetadata> {
    return this.client.getFeatureServiceMetadata(this.serviceId);
  }

  public async mapServiceMetadata(): Promise<HonuaServiceMetadata> {
    return this.client.getMapServiceMetadata(this.serviceId);
  }

  public async featureLayerIds(): Promise<number[]> {
    const metadata = await this.featureServiceMetadata();
    return extractLayerIds(metadata);
  }

  public async featureLayers(): Promise<HonuaFeatureLayer[]> {
    const ids = await this.featureLayerIds();
    return ids.map((layerId) =>
      new HonuaFeatureLayer({
        client: this.client,
        serviceId: this.serviceId,
        layerId,
      }),
    );
  }

  public async mapLayerIds(): Promise<number[]> {
    const metadata = await this.mapServiceMetadata();
    return extractLayerIds(metadata);
  }

  public async mapLayers(): Promise<HonuaMapLayer[]> {
    const ids = await this.mapLayerIds();
    return ids.map((layerId) =>
      new HonuaMapLayer({
        client: this.client,
        serviceId: this.serviceId,
        layerId,
      }),
    );
  }

  public async request<T = unknown>(request: HonuaServiceRequest): Promise<T> {
    return this.client.request<T>({
      ...request,
      path: `/rest/services/${encodeURIComponent(this.serviceId)}/${normalizeServicePath(request.path)}`,
    });
  }

  public mapService(): HonuaMapService {
    return new HonuaMapService({
      client: this.client,
      serviceId: this.serviceId,
    });
  }

  public mapLayer(layerId: number): HonuaMapLayer {
    return new HonuaMapLayer({
      client: this.client,
      serviceId: this.serviceId,
      layerId,
    });
  }
}

export interface HonuaFeatureLayerOptions {
  client: HonuaClient;
  serviceId: string;
  layerId: number;
}

export class HonuaFeatureLayer<T = Record<string, unknown>> {
  public readonly client: HonuaClient;
  public readonly serviceId: string;
  public readonly layerId: number;

  public constructor(options: HonuaFeatureLayerOptions) {
    this.client = options.client;
    this.serviceId = options.serviceId;
    this.layerId = options.layerId;
  }

  public async metadata(): Promise<HonuaLayerMetadata> {
    return this.client.getLayerMetadata(this.serviceId, this.layerId);
  }

  public createQuery(): HonuaFeatureLayerQueryRequest {
    return {
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    };
  }

  public async queryFeatures(
    request: HonuaFeatureLayerQueryRequest = {},
  ): Promise<HonuaTypedQueryResponse<T>> {
    return this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      ...request,
    }) as Promise<HonuaTypedQueryResponse<T>>;
  }

  public async queryFeaturesAll(
    request: HonuaFeatureLayerQueryAllRequest = {},
  ): Promise<HonuaTypedFeature<T>[]> {
    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    const features: HonuaTypedFeature<T>[] = [];
    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = response.features ?? [];
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < pageSize) {
        break;
      }
    }

    return features;
  }

  public async *queryFeaturesStream(
    request: HonuaFeatureLayerQueryAllRequest = {},
  ): AsyncGenerator<HonuaTypedFeature<T>[], void, undefined> {
    // Use server streaming RPC when gRPC-Web transport is active
    if (this.client.isGrpcWeb) {
      yield* this.client.queryFeaturesStream({
        serviceId: this.serviceId,
        layerId: this.layerId,
        where: request.where,
        outFields: request.outFields,
        returnGeometry: request.returnGeometry,
        orderByFields: request.orderByFields,
        objectIds: request.objectIds,
        geometry: request.geometry,
        geometryType: request.geometryType,
        spatialRel: request.spatialRel,
        returnDistinctValues: request.returnDistinctValues,
        resultRecordCount: request.resultRecordCount,
      }) as AsyncGenerator<HonuaTypedFeature<T>[], void, undefined>;
      return;
    }

    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = response.features ?? [];
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async queryFeatureCount(
    request: HonuaFeatureLayerQueryCountRequest = {},
  ): Promise<number> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnCountOnly: true,
        ...request.extraParams,
      },
    });

    if (isObject(response) && typeof response.count === "number" && Number.isFinite(response.count)) {
      return response.count;
    }
    if (isObject(response) && Array.isArray(response.features)) {
      return response.features.length;
    }
    return 0;
  }

  public async queryObjectIds(
    request: HonuaFeatureLayerQueryObjectIdsRequest = {},
  ): Promise<number[]> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnIdsOnly: true,
        ...request.extraParams,
      },
    });

    if (isObject(response) && Array.isArray(response.objectIds)) {
      return response.objectIds
        .map((value) => Number(value))
        .filter((value): value is number => Number.isFinite(value));
    }
    return [];
  }

  public async queryExtent(
    request: HonuaFeatureLayerQueryExtentRequest = {},
  ): Promise<HonuaFeatureLayerQueryExtentResponse> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      method: request.method,
      extraParams: {
        returnExtentOnly: true,
        ...request.extraParams,
      },
    });

    return extractExtentFromResponse(response);
  }

  public async queryRelatedRecords(
    request: HonuaFeatureLayerQueryRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.client.queryRelatedRecords({
      serviceId: this.serviceId,
      layerId: this.layerId,
      ...request,
    });
  }

  public async queryRelatedFeatures(
    request: HonuaFeatureLayerQueryRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.queryRelatedRecords(request);
  }

  public async applyEdits(
    request: HonuaFeatureLayerApplyEditsRequest,
  ): Promise<HonuaApplyEditsResponse> {
    return this.client.applyEdits({
      serviceId: this.serviceId,
      layerId: this.layerId,
      ...request,
    });
  }

  public async queryAttachments(
    request: HonuaFeatureLayerQueryAttachmentsRequest = {},
  ): Promise<HonuaQueryAttachmentsResponse> {
    const method: QueryMethod = request.method ?? "GET";
    const path =
      `/rest/services/${encodeURIComponent(this.serviceId)}` +
      `/FeatureServer/${this.layerId}/queryAttachments`;
    const query = {
      ...(request.objectIds === undefined
        ? {}
        : {
            objectIds: normalizeObjectIds(request.objectIds),
          }),
      ...(request.where === undefined ? {} : { where: request.where }),
      ...(request.extraParams ?? {}),
    };

    if (method === "GET") {
      return this.client.request({
        method: "GET",
        path,
        responseFormat: request.responseFormat ?? "json",
        query,
      }) as Promise<HonuaQueryAttachmentsResponse>;
    }

    const body = toFormBody({
      f: request.responseFormat ?? "json",
      ...query,
    });
    return this.client.request({
      method: "POST",
      path,
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body,
    }) as Promise<HonuaQueryAttachmentsResponse>;
  }

  public async listAttachments(
    request: HonuaFeatureLayerListAttachmentsRequest,
  ): Promise<HonuaAttachmentListResponse> {
    return this.client.request({
      method: "GET",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${request.objectId}/attachments`,
      responseFormat: request.responseFormat ?? "json",
      query: request.extraParams,
    }) as Promise<HonuaAttachmentListResponse>;
  }

  public async deleteAttachments(
    request: HonuaFeatureLayerDeleteAttachmentsRequest,
  ): Promise<HonuaDeleteAttachmentsResponse> {
    const body = toFormBody({
      f: request.responseFormat ?? "json",
      attachmentIds: normalizeObjectIds(request.attachmentIds),
      ...(request.extraParams ?? {}),
    });
    return this.client.request<HonuaDeleteAttachmentsResponse>({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${request.objectId}/deleteAttachments`,
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body,
    });
  }

  public async addAttachment(
    request: HonuaFeatureLayerAddAttachmentRequest,
  ): Promise<HonuaAddAttachmentResponse> {
    const form = buildAttachmentFormData(request);
    return this.client.request<HonuaAddAttachmentResponse>({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${request.objectId}/addAttachment`,
      responseFormat: request.responseFormat ?? "json",
      query: request.extraParams,
      body: form,
    });
  }

  public async updateAttachment(
    request: HonuaFeatureLayerUpdateAttachmentRequest,
  ): Promise<HonuaUpdateAttachmentResponse> {
    const form = buildAttachmentFormData(request);
    form.set("attachmentId", String(request.attachmentId));
    return this.client.request<HonuaUpdateAttachmentResponse>({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${request.objectId}/updateAttachment`,
      responseFormat: request.responseFormat ?? "json",
      query: request.extraParams,
      body: form,
    });
  }

  public async request<T = unknown>(
    request: HonuaFeatureLayerRequest,
  ): Promise<T> {
    return this.client.request<T>({
      ...request,
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${normalizeLayerPath(request.path)}`,
    });
  }
}

export interface HonuaMapServiceOptions {
  client: HonuaClient;
  serviceId: string;
}

export class HonuaMapService {
  public readonly client: HonuaClient;
  public readonly serviceId: string;

  public constructor(options: HonuaMapServiceOptions) {
    this.client = options.client;
    this.serviceId = options.serviceId;
  }

  public async metadata(): Promise<HonuaServiceMetadata> {
    return this.client.getMapServiceMetadata(this.serviceId);
  }

  public layer(layerId: number): HonuaMapLayer {
    return new HonuaMapLayer({
      client: this.client,
      serviceId: this.serviceId,
      layerId,
    });
  }

  public async layerIds(): Promise<number[]> {
    const metadata = await this.metadata();
    return extractLayerIds(metadata);
  }

  public async layers(): Promise<HonuaMapLayer[]> {
    const ids = await this.layerIds();
    return ids.map((layerId) =>
      new HonuaMapLayer({
        client: this.client,
        serviceId: this.serviceId,
        layerId,
      }),
    );
  }

  public async exportMap(
    request: HonuaMapServiceExportMapRequest,
  ): Promise<HonuaExportMapResponse> {
    return this.client.exportMap({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async legend(
    request: HonuaMapServiceLegendRequest = {},
  ): Promise<HonuaLegendResponse> {
    return this.client.getMapLegend({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async getLegend(
    request: HonuaMapServiceLegendRequest = {},
  ): Promise<HonuaLegendResponse> {
    return this.legend(request);
  }

  public async identify(
    request: HonuaMapServiceIdentifyRequest,
  ): Promise<HonuaIdentifyResponse> {
    return this.client.identifyMap({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async find(
    request: HonuaMapServiceFindRequest,
  ): Promise<HonuaFindResponse> {
    return this.client.findMap({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async queryLayer(
    request: HonuaMapServiceQueryLayerRequest,
  ): Promise<HonuaQueryResponse> {
    return this.client.queryMapLayer({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async queryLayerRelatedRecords(
    request: HonuaMapServiceQueryLayerRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.client.queryMapRelatedRecords({
      serviceId: this.serviceId,
      ...request,
    });
  }

  public async queryLayerRelatedFeatures(
    request: HonuaMapServiceQueryLayerRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.queryLayerRelatedRecords(request);
  }

  public async queryLayerFeaturesAll(
    request: HonuaMapServiceQueryLayerAllRequest,
  ): Promise<HonuaFeature[]> {
    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    const features: HonuaFeature[] = [];
    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryLayer({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeaturesFromResponse(response);
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < pageSize) {
        break;
      }
    }

    return features;
  }

  public async *queryLayerFeaturesStream(
    request: HonuaMapServiceQueryLayerAllRequest,
  ): AsyncGenerator<HonuaFeature[], void, undefined> {
    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryLayer({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeaturesFromResponse(response);
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async queryLayerFeatureCount(
    request: HonuaMapServiceQueryLayerCountRequest,
  ): Promise<number> {
    const response = await this.queryLayer({
      layerId: request.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnCountOnly: true,
        ...request.extraParams,
      },
    });
    return extractFeatureCountFromResponse(response);
  }

  public async queryLayerObjectIds(
    request: HonuaMapServiceQueryLayerObjectIdsRequest,
  ): Promise<number[]> {
    const response = await this.queryLayer({
      layerId: request.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnIdsOnly: true,
        ...request.extraParams,
      },
    });
    return extractObjectIdsFromResponse(response);
  }

  public async queryLayerExtent(
    request: HonuaMapServiceQueryLayerExtentRequest,
  ): Promise<HonuaMapServiceQueryLayerExtentResponse> {
    const response = await this.queryLayer({
      layerId: request.layerId,
      where: request.where ?? "1=1",
      returnGeometry: false,
      method: request.method,
      extraParams: {
        returnExtentOnly: true,
        ...request.extraParams,
      },
    });
    return extractExtentFromResponse(response);
  }

  public async exportImage(
    request: HonuaMapServiceExportMapRequest,
  ): Promise<HonuaExportMapResponse> {
    return this.exportMap(request);
  }

  public async request<T = unknown>(
    request: HonuaMapServiceRequest,
  ): Promise<T> {
    return this.client.request<T>({
      ...request,
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/MapServer/${normalizeServicePath(request.path)}`,
    });
  }
}

export interface HonuaMapLayerOptions {
  client: HonuaClient;
  serviceId: string;
  layerId: number;
}

export class HonuaMapLayer {
  public readonly client: HonuaClient;
  public readonly serviceId: string;
  public readonly layerId: number;

  public constructor(options: HonuaMapLayerOptions) {
    this.client = options.client;
    this.serviceId = options.serviceId;
    this.layerId = options.layerId;
  }

  public async metadata(): Promise<HonuaLayerMetadata> {
    return this.client.request({
      method: "GET",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/MapServer/${this.layerId}`,
    }) as Promise<HonuaLayerMetadata>;
  }

  public createQuery(): HonuaMapLayerQueryRequest {
    return {
      where: "1=1",
      outFields: ["*"],
      returnGeometry: true,
    };
  }

  public async queryFeatures(
    request: HonuaMapLayerQueryRequest = {},
  ): Promise<HonuaQueryResponse> {
    return this.client.queryMapLayer({
      serviceId: this.serviceId,
      layerId: this.layerId,
      ...request,
    });
  }

  public async queryRelatedRecords(
    request: HonuaMapLayerQueryRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.client.queryMapRelatedRecords({
      serviceId: this.serviceId,
      layerId: this.layerId,
      ...request,
    });
  }

  public async queryRelatedFeatures(
    request: HonuaMapLayerQueryRelatedRecordsRequest,
  ): Promise<HonuaRelatedRecordsResponse> {
    return this.queryRelatedRecords(request);
  }

  public async queryFeaturesAll(
    request: HonuaMapLayerQueryAllRequest = {},
  ): Promise<HonuaFeature[]> {
    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    const features: HonuaFeature[] = [];
    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeaturesFromResponse(response);
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < pageSize) {
        break;
      }
    }

    return features;
  }

  public async *queryFeaturesStream(
    request: HonuaMapLayerQueryAllRequest = {},
  ): AsyncGenerator<HonuaFeature[], void, undefined> {
    const pageSize =
      typeof request.pageSize === "number" && Number.isFinite(request.pageSize)
        ? Math.max(1, Math.trunc(request.pageSize))
        : 2000;
    const maxPages =
      typeof request.maxPages === "number" && Number.isFinite(request.maxPages)
        ? Math.max(1, Math.trunc(request.maxPages))
        : 100;

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...request,
        extraParams: {
          ...(request.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeaturesFromResponse(response);
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async queryFeatureCount(
    request: HonuaMapLayerQueryCountRequest = {},
  ): Promise<number> {
    const response = await this.queryFeatures({
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnCountOnly: true,
        ...request.extraParams,
      },
    });
    return extractFeatureCountFromResponse(response);
  }

  public async queryObjectIds(
    request: HonuaMapLayerQueryObjectIdsRequest = {},
  ): Promise<number[]> {
    const response = await this.queryFeatures({
      where: request.where ?? "1=1",
      returnGeometry: false,
      outFields: "OBJECTID",
      method: request.method,
      extraParams: {
        returnIdsOnly: true,
        ...request.extraParams,
      },
    });
    return extractObjectIdsFromResponse(response);
  }

  public async queryExtent(
    request: HonuaMapLayerQueryExtentRequest = {},
  ): Promise<HonuaMapLayerQueryExtentResponse> {
    const response = await this.queryFeatures({
      where: request.where ?? "1=1",
      returnGeometry: false,
      method: request.method,
      extraParams: {
        returnExtentOnly: true,
        ...request.extraParams,
      },
    });
    return extractExtentFromResponse(response);
  }

  public async request<T = unknown>(
    request: HonuaMapLayerRequest,
  ): Promise<T> {
    return this.client.request<T>({
      ...request,
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/MapServer/${this.layerId}/${normalizeLayerPath(request.path)}`,
    });
  }
}

export interface HonuaOgcFeaturesOptions {
  client: HonuaClient;
}

export interface HonuaOgcFeatureCollectionOptions {
  client: HonuaClient;
  collectionId: string | number;
}

export class HonuaOgcFeatures {
  public readonly client: HonuaClient;

  public constructor(options: HonuaOgcFeaturesOptions) {
    this.client = options.client;
  }

  public collection(collectionId: string | number): HonuaOgcFeatureCollection {
    return new HonuaOgcFeatureCollection({
      client: this.client,
      collectionId,
    });
  }

  public async landing(request: HonuaOgcMetadataRequest = {}): Promise<HonuaOgcLandingResponse> {
    return this.client.getOgcFeaturesLanding(request);
  }

  public async conformance(request: HonuaOgcMetadataRequest = {}): Promise<HonuaOgcConformanceResponse> {
    return this.client.getOgcFeaturesConformance(request);
  }

  public async collections(request: HonuaOgcMetadataRequest = {}): Promise<HonuaOgcCollectionsResponse> {
    return this.client.listOgcCollections(request);
  }

  public async getCollection(request: HonuaOgcCollectionRequest): Promise<HonuaOgcCollectionMetadata> {
    return this.client.getOgcCollection(request);
  }

  public async queryables(request: HonuaOgcCollectionRequest): Promise<HonuaOgcQueryablesResponse> {
    return this.client.getOgcQueryables(request);
  }

  public async items(request: HonuaOgcItemsRequest): Promise<HonuaOgcFeatureCollectionResponse> {
    return this.client.listOgcItems(request);
  }

  public async itemsAll(
    request: HonuaOgcItemsAllRequest,
  ): Promise<HonuaOgcFeatureResponse[]> {
    const pageSize = normalizePageSize(request.pageSize, request.limit);
    const maxPages = normalizeMaxPages(request.maxPages);
    const offset = normalizeOffset(request.offset);
    const totalLimit = normalizeTotalLimit(request.limit);
    const features: HonuaOgcFeatureResponse[] = [];

    for (let page = 0; page < maxPages; page += 1) {
      if (totalLimit !== undefined && features.length >= totalLimit) {
        break;
      }
      const remainingLimit =
        totalLimit === undefined ? pageSize : Math.max(0, totalLimit - features.length);
      if (remainingLimit < 1) {
        break;
      }

      const limit = Math.min(pageSize, remainingLimit);
      const response = await this.items({
        ...request,
        limit,
        offset: offset + page * pageSize,
      });
      const pageFeatures = extractOgcFeatures(response);
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < limit) {
        break;
      }
    }

    if (totalLimit !== undefined && features.length > totalLimit) {
      return features.slice(0, totalLimit);
    }
    return features;
  }

  public async item(request: HonuaOgcItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.getOgcItem(request);
  }

  public async createItem(request: HonuaOgcCreateItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.createOgcItem(request);
  }

  public async replaceItem(request: HonuaOgcReplaceItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.replaceOgcItem(request);
  }

  public async patchItem(request: HonuaOgcPatchItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.patchOgcItem(request);
  }

  public async deleteItem(request: HonuaOgcDeleteItemRequest): Promise<void> {
    return this.client.deleteOgcItem(request);
  }
}

export class HonuaOgcFeatureCollection {
  public readonly client: HonuaClient;
  public readonly collectionId: string | number;

  public constructor(options: HonuaOgcFeatureCollectionOptions) {
    this.client = options.client;
    this.collectionId = options.collectionId;
  }

  public async metadata(request: HonuaOgcMetadataRequest = {}): Promise<HonuaOgcCollectionMetadata> {
    return this.client.getOgcCollection({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async queryables(request: HonuaOgcMetadataRequest = {}): Promise<HonuaOgcQueryablesResponse> {
    return this.client.getOgcQueryables({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async items(request: HonuaOgcCollectionItemsRequest = {}): Promise<HonuaOgcFeatureCollectionResponse> {
    return this.client.listOgcItems({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async itemsAll(
    request: HonuaOgcCollectionItemsAllRequest = {},
  ): Promise<HonuaOgcFeatureResponse[]> {
    const pageSize = normalizePageSize(request.pageSize, request.limit);
    const maxPages = normalizeMaxPages(request.maxPages);
    const offset = normalizeOffset(request.offset);
    const totalLimit = normalizeTotalLimit(request.limit);
    const features: HonuaOgcFeatureResponse[] = [];

    for (let page = 0; page < maxPages; page += 1) {
      if (totalLimit !== undefined && features.length >= totalLimit) {
        break;
      }
      const remainingLimit =
        totalLimit === undefined ? pageSize : Math.max(0, totalLimit - features.length);
      if (remainingLimit < 1) {
        break;
      }

      const limit = Math.min(pageSize, remainingLimit);
      const response = await this.items({
        ...request,
        limit,
        offset: offset + page * pageSize,
      });
      const pageFeatures = extractOgcFeatures(response);
      if (pageFeatures.length === 0) {
        break;
      }

      features.push(...pageFeatures);
      if (pageFeatures.length < limit) {
        break;
      }
    }

    if (totalLimit !== undefined && features.length > totalLimit) {
      return features.slice(0, totalLimit);
    }
    return features;
  }

  public async *itemsStream(
    request: HonuaOgcCollectionItemsAllRequest = {},
  ): AsyncGenerator<HonuaOgcFeatureResponse[], void, undefined> {
    const pageSize = normalizePageSize(request.pageSize, request.limit);
    const maxPages = normalizeMaxPages(request.maxPages);
    const offset = normalizeOffset(request.offset);

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.items({
        ...request,
        limit: pageSize,
        offset: offset + page * pageSize,
      });
      const pageFeatures = extractOgcFeatures(response);
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async item(request: HonuaOgcCollectionItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.getOgcItem({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async createItem(request: HonuaOgcCollectionCreateItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.createOgcItem({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async replaceItem(request: HonuaOgcCollectionReplaceItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.replaceOgcItem({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async patchItem(request: HonuaOgcCollectionPatchItemRequest): Promise<HonuaOgcFeatureResponse> {
    return this.client.patchOgcItem({
      ...request,
      collectionId: this.collectionId,
    });
  }

  public async deleteItem(request: HonuaOgcCollectionDeleteItemRequest): Promise<void> {
    return this.client.deleteOgcItem({
      ...request,
      collectionId: this.collectionId,
    });
  }
}

export function createHonuaService(
  client: HonuaClient,
  serviceId: string,
): HonuaService {
  return new HonuaService({
    client,
    serviceId,
  });
}

export function createHonuaOgcFeatures(
  client: HonuaClient,
): HonuaOgcFeatures {
  return new HonuaOgcFeatures({
    client,
  });
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function extractFeatureCountFromResponse(response: unknown): number {
  if (isObject(response) && typeof response.count === "number" && Number.isFinite(response.count)) {
    return response.count;
  }
  if (isObject(response) && Array.isArray(response.features)) {
    return response.features.length;
  }
  return 0;
}

function extractFeaturesFromResponse(response: unknown): HonuaFeature[] {
  if (!isObject(response) || !Array.isArray(response.features)) {
    return [];
  }
  return response.features as HonuaFeature[];
}

function extractObjectIdsFromResponse(response: unknown): number[] {
  if (!isObject(response) || !Array.isArray(response.objectIds)) {
    return [];
  }
  return response.objectIds
    .map((value) => Number(value))
    .filter((value): value is number => Number.isFinite(value));
}

function extractExtentFromResponse(response: unknown): { extent: HonuaExtent | null; count?: number } {
  if (!isObject(response)) {
    return { extent: null };
  }
  const count = isFiniteNumber(response.count) ? response.count : undefined;
  const extent = isObject(response.extent) ? (response.extent as unknown as HonuaExtent) : null;
  return { extent, count };
}

function normalizeObjectIds(ids: readonly number[] | string): string {
  return Array.isArray(ids) ? ids.join(",") : String(ids);
}

function toFormBody(values: Record<string, string | number | boolean>): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    params.set(key, String(value));
  }
  return params.toString();
}

function normalizeLayerPath(path: string): string {
  return path.startsWith("/") ? path.slice(1) : path;
}

function normalizeServicePath(path: string): string {
  return path.startsWith("/") ? path.slice(1) : path;
}

function normalizePageSize(pageSize: number | undefined, limit: number | undefined): number {
  if (isFinitePositiveInteger(pageSize)) {
    return pageSize;
  }
  if (isFinitePositiveInteger(limit)) {
    return limit;
  }
  return 100;
}

function normalizeMaxPages(maxPages: number | undefined): number {
  if (isFinitePositiveInteger(maxPages)) {
    return maxPages;
  }
  return 100;
}

function normalizeOffset(offset: number | undefined): number {
  if (typeof offset !== "number" || !Number.isFinite(offset)) {
    return 0;
  }
  return Math.max(0, Math.trunc(offset));
}

function normalizeTotalLimit(limit: number | undefined): number | undefined {
  if (!isFinitePositiveInteger(limit)) {
    return undefined;
  }
  return limit;
}

function isFinitePositiveInteger(value: number | undefined): value is number {
  return typeof value === "number" && Number.isFinite(value) && Math.trunc(value) > 0;
}

function buildAttachmentFormData(request: {
  attachment: HonuaFeatureLayerAttachmentData;
  name?: string;
  contentType?: string;
}): FormData {
  const form = new FormData();
  if (request.attachment instanceof Blob) {
    const blob = ensureBlobType(request.attachment, request.contentType);
    const fileName = request.name ?? resolveBlobName(request.attachment);
    form.set("attachment", blob, fileName);
    return form;
  }

  const blob = new Blob([request.attachment], {
    type: request.contentType ?? "application/octet-stream",
  });
  form.set("attachment", blob, request.name ?? "attachment.txt");
  return form;
}

function resolveBlobName(blob: Blob): string {
  if ("name" in blob && typeof (blob as File).name === "string" && (blob as File).name.length > 0) {
    return (blob as File).name;
  }
  return "attachment.bin";
}

function ensureBlobType(blob: Blob, contentType: string | undefined): Blob {
  if (!contentType || blob.type === contentType) {
    return blob;
  }
  return new Blob([blob], { type: contentType });
}

function extractLayerIds(metadata: unknown): number[] {
  if (!isObject(metadata) || !Array.isArray(metadata.layers)) {
    return [];
  }
  const ids: number[] = [];
  for (const layer of metadata.layers) {
    if (!isObject(layer)) {
      continue;
    }
    const parsed = Number(layer.id);
    if (!Number.isFinite(parsed)) {
      continue;
    }
    ids.push(Math.trunc(parsed));
  }
  return ids;
}

function extractOgcFeatures(response: unknown): HonuaOgcFeatureResponse[] {
  if (!isObject(response) || !Array.isArray(response.features)) {
    return [];
  }
  return response.features as HonuaOgcFeatureResponse[];
}
