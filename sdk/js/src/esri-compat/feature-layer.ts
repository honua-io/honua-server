import { HonuaClient } from "../core/client.js";
import type { QueryMethod } from "../core/types.js";
import { CompatEventBus, resolveCompatEventBus, safeInvokeCompatListener } from "./event-bus.js";
import { parseFeatureLayerUrl } from "./url.js";

const DEFAULT_MAX_ATTACHMENT_BYTES = 25 * 1024 * 1024;

export interface FeatureLayerCompatOptions {
  url: string;
  id?: string;
  title?: string;
  outFields?: string | string[];
  definitionExpression?: string;
  renderer?: unknown;
  popupTemplate?: unknown;
  labelingInfo?: unknown[];
  labelsVisible?: boolean;
  opacity?: number;
  visible?: boolean;
  minScale?: number;
  maxScale?: number;
  legendEnabled?: boolean;
  listMode?: string;
  maxAttachmentBytes?: number;
  client?: HonuaClient;
  eventBus?: CompatEventBus;
}

export interface FeatureLayerQueryOptions {
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

export type FeatureLayerQueryAllOptions = FeatureLayerQueryOptions & {
  pageSize?: number;
  maxPages?: number;
};

export interface FeatureLayerEditsOptions {
  adds?: unknown[];
  updates?: unknown[];
  deletes?: number[] | string;
  rollbackOnFailure?: boolean;
}

export interface FeatureLayerCreateQueryResult {
  where: string;
  outFields: string[];
  returnGeometry: boolean;
}

export interface FeatureLayerQueryCountOptions {
  where?: string;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerQueryRelatedFeaturesOptions {
  relationshipId: number;
  objectIds?: number[] | string;
  where?: string;
  outFields?: string | string[];
  returnGeometry?: boolean;
  method?: QueryMethod;
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerQueryAttachmentsOptions {
  objectIds?: number[] | string;
  where?: string;
  method?: QueryMethod;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerListAttachmentsOptions {
  objectId: number;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerDeleteAttachmentsOptions {
  objectId: number;
  attachmentIds: number[] | string;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}

export type FeatureLayerAttachmentData = Blob | ArrayBuffer | ArrayBufferView | string;

export interface FeatureLayerAddAttachmentOptions {
  objectId: number;
  attachment: FeatureLayerAttachmentData;
  name?: string;
  contentType?: string;
  maxAttachmentBytes?: number;
  responseFormat?: "json" | "pjson";
  extraParams?: Record<string, string | number | boolean>;
}

export interface FeatureLayerUpdateAttachmentOptions extends FeatureLayerAddAttachmentOptions {
  attachmentId: number;
}

export interface FeatureLayerQueryExtentResult {
  extent: unknown | null;
  count?: number;
}

export type FeatureLayerLoadStatusCompat = "not-loaded" | "loading" | "loaded" | "failed";

export interface FeatureLayerHandleCompat {
  remove(): void;
}

export class FeatureLayerCompat {
  public readonly url: string;
  public id: string;
  public title: string | undefined;
  public readonly serviceId: string;
  public readonly layerId: number;
  public outFields: string[] | undefined;
  public definitionExpression: string | undefined;
  public renderer: unknown;
  public popupTemplate: unknown;
  public labelingInfo: unknown[];
  public labelsVisible: boolean;
  public opacity: number;
  public visible: boolean;
  public minScale: number;
  public maxScale: number;
  public legendEnabled: boolean;
  public listMode: string;
  public loaded: boolean;
  public loadStatus: FeatureLayerLoadStatusCompat;
  public metadata: unknown;
  public timeExtent: { start: Date; end: Date } | undefined;
  public readonly eventBus: CompatEventBus;

  private readonly client: HonuaClient;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;
  private readonly eventListeners: Map<string, Set<(event: unknown) => void>>;
  private readonly maxAttachmentBytes: number;

  public constructor(options: FeatureLayerCompatOptions) {
    const parsed = parseFeatureLayerUrl(options.url);
    this.url = options.url;
    this.serviceId = parsed.serviceId;
    this.layerId = parsed.layerId;
    this.id = options.id ?? `${this.serviceId}-${this.layerId}`;
    this.title = options.title;
    this.outFields =
      options.outFields === undefined
        ? undefined
        : Array.isArray(options.outFields)
          ? [...options.outFields]
          : [options.outFields];
    this.definitionExpression = options.definitionExpression;
    this.renderer = options.renderer;
    this.popupTemplate = options.popupTemplate;
    this.labelingInfo = Array.isArray(options.labelingInfo) ? [...options.labelingInfo] : [];
    this.labelsVisible = options.labelsVisible ?? true;
    this.opacity = normalizeOpacity(options.opacity ?? 1);
    this.visible = options.visible ?? true;
    this.minScale = normalizeScale(options.minScale);
    this.maxScale = normalizeScale(options.maxScale);
    this.legendEnabled = options.legendEnabled ?? true;
    this.listMode = options.listMode ?? "show";
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.metadata = undefined;
    this.timeExtent = undefined;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.client) ?? new CompatEventBus();
    this.client = options.client ?? new HonuaClient({ baseUrl: parsed.baseUrl });
    this.watchListeners = new Map();
    this.eventListeners = new Map();
    this.maxAttachmentBytes = normalizeAttachmentSizeLimit(options.maxAttachmentBytes);
  }

  public async load(): Promise<FeatureLayerCompat> {
    if (!this.loaded) {
      this.loadStatus = "loading";
      this.notifyWatchers("loadStatus", this.loadStatus);
      this.eventBus.emit(
        "feature-layer.loading",
        { serviceId: this.serviceId, layerId: this.layerId, id: this.id },
        this,
      );
      try {
        this.metadata = await this.client.getLayerMetadata(this.serviceId, this.layerId);
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = true;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "loaded";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit(
          "feature-layer.loaded",
          { serviceId: this.serviceId, layerId: this.layerId, id: this.id },
          this,
        );
      } catch (error) {
        this.metadata = undefined;
        this.notifyWatchers("metadata", this.metadata);
        this.loaded = false;
        this.notifyWatchers("loaded", this.loaded);
        this.loadStatus = "failed";
        this.notifyWatchers("loadStatus", this.loadStatus);
        this.eventBus.emit(
          "feature-layer.failed",
          { serviceId: this.serviceId, layerId: this.layerId, id: this.id, error },
          this,
        );
        throw error;
      }
    }
    return this;
  }

  public async when(callback?: (layer: FeatureLayerCompat) => void): Promise<FeatureLayerCompat> {
    const layer = await this.load();
    if (callback) {
      callback(layer);
    }

    return layer;
  }

  public refresh(): void {
    this.loaded = false;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "not-loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.metadata = undefined;
    this.notifyWatchers("metadata", this.metadata);
    this.eventBus.emit(
      "feature-layer.refreshed",
      { serviceId: this.serviceId, layerId: this.layerId, id: this.id },
      this,
    );
  }

  public watch(propertyName: string, listener: (value: unknown) => void): FeatureLayerHandleCompat {
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

  public setVisibility(visible: boolean): void {
    this.visible = visible;
    this.notifyWatchers("visible", this.visible);
    this.eventBus.emit(
      "layer.visibility-changed",
      { layerId: this.id, serviceId: this.serviceId, sublayerId: this.layerId, visible },
      this,
    );
  }

  public setOpacity(opacity: number): void {
    this.opacity = normalizeOpacity(opacity);
    this.notifyWatchers("opacity", this.opacity);
    this.eventBus.emit(
      "layer.opacity-changed",
      { layerId: this.id, serviceId: this.serviceId, sublayerId: this.layerId, opacity: this.opacity },
      this,
    );
  }

  public setRenderer(renderer: unknown): void {
    this.renderer = renderer;
    this.notifyWatchers("renderer", this.renderer);
    this.eventBus.emit("feature-layer.renderer-changed", { layerId: this.id }, this);
  }

  public setPopupTemplate(popupTemplate: unknown): void {
    this.popupTemplate = popupTemplate;
    this.notifyWatchers("popupTemplate", this.popupTemplate);
    this.eventBus.emit("feature-layer.popup-template-changed", { layerId: this.id }, this);
  }

  public setLabelingInfo(labelingInfo: readonly unknown[]): void {
    this.labelingInfo = [...labelingInfo];
    this.notifyWatchers("labelingInfo", this.labelingInfo);
    this.eventBus.emit("feature-layer.labeling-changed", { layerId: this.id }, this);
  }

  public setDefinitionExpression(definitionExpression: string | undefined): void {
    this.definitionExpression = definitionExpression;
    this.notifyWatchers("definitionExpression", this.definitionExpression);
    this.eventBus.emit(
      "feature-layer.definition-expression-changed",
      { layerId: this.id, definitionExpression },
      this,
    );
  }

  public setOutFields(outFields: string | readonly string[] | undefined): void {
    this.outFields =
      outFields === undefined ? undefined : Array.isArray(outFields) ? [...outFields] : [outFields];
    this.notifyWatchers("outFields", this.outFields);
    this.eventBus.emit("feature-layer.out-fields-changed", { layerId: this.id, outFields: this.outFields }, this);
  }

  public setLabelsVisible(labelsVisible: boolean): void {
    this.labelsVisible = labelsVisible;
    this.notifyWatchers("labelsVisible", this.labelsVisible);
    this.eventBus.emit("feature-layer.labels-visible-changed", { layerId: this.id, labelsVisible }, this);
  }

  public setScaleRange(minScale: number | undefined, maxScale: number | undefined): void {
    this.minScale = normalizeScale(minScale);
    this.maxScale = normalizeScale(maxScale);
    this.notifyWatchers("minScale", this.minScale);
    this.notifyWatchers("maxScale", this.maxScale);
    this.eventBus.emit(
      "feature-layer.scale-range-changed",
      { layerId: this.id, minScale: this.minScale, maxScale: this.maxScale },
      this,
    );
  }

  public setLegendEnabled(legendEnabled: boolean): void {
    this.legendEnabled = legendEnabled;
    this.notifyWatchers("legendEnabled", this.legendEnabled);
    this.eventBus.emit("feature-layer.legend-enabled-changed", { layerId: this.id, legendEnabled }, this);
  }

  public setTimeExtent(extent: { start: Date; end: Date } | undefined): void {
    this.timeExtent = extent
      ? { start: new Date(extent.start.getTime()), end: new Date(extent.end.getTime()) }
      : undefined;
    this.notifyWatchers("timeExtent", this.timeExtent);
    this.eventBus.emit("feature-layer.time-extent-change", { layerId: this.id, timeExtent: this.timeExtent }, this);
  }

  public on(eventName: string, listener: (event: unknown) => void): FeatureLayerHandleCompat {
    const namespacedEvent = `feature-layer.${eventName}`;
    let listeners = this.eventListeners.get(eventName);
    if (!listeners) {
      listeners = new Set();
      this.eventListeners.set(eventName, listeners);
    }
    listeners.add(listener);

    const subscription = this.eventBus.on(namespacedEvent, (event) => {
      safeInvokeCompatListener(listener, event.payload);
    });

    return {
      remove: () => {
        listeners?.delete(listener);
        subscription.remove();
      },
    };
  }

  public listFields(): readonly Record<string, unknown>[] {
    return extractFieldDefinitions(this.metadata);
  }

  public getField(fieldName: string): Record<string, unknown> | undefined {
    const normalizedFieldName = fieldName.trim();
    if (normalizedFieldName.length === 0) {
      return undefined;
    }

    return this.listFields().find((field) => {
      const candidate = field.name;
      return typeof candidate === "string" && candidate.trim() === normalizedFieldName;
    });
  }

  public hasField(fieldName: string): boolean {
    return this.getField(fieldName) !== undefined;
  }

  public createQuery(): FeatureLayerCreateQueryResult {
    return {
      where: this.definitionExpression ?? "1=1",
      outFields: this.outFields ? [...this.outFields] : ["*"],
      returnGeometry: true,
    };
  }

  public queryFeatures(options: FeatureLayerQueryOptions = {}): Promise<unknown> {
    const timeParam = buildTimeParam(this.timeExtent, options.extraParams);
    return this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      outFields: options.outFields ?? this.outFields,
      returnGeometry: options.returnGeometry,
      method: options.method,
      extraParams: timeParam
        ? { ...(options.extraParams ?? {}), time: timeParam }
        : options.extraParams,
    });
  }

  public async queryFeaturesAll(options: FeatureLayerQueryAllOptions = {}): Promise<unknown[]> {
    const { pageSize: requestedPageSize, maxPages: requestedMaxPages, ...queryOptions } = options;
    const pageSize =
      typeof requestedPageSize === "number" && Number.isFinite(requestedPageSize)
        ? Math.max(1, Math.trunc(requestedPageSize))
        : 2000;
    const maxPages =
      typeof requestedMaxPages === "number" && Number.isFinite(requestedMaxPages)
        ? Math.max(1, Math.trunc(requestedMaxPages))
        : 100;

    const features: unknown[] = [];
    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...queryOptions,
        extraParams: {
          ...(queryOptions.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeatures(response) ?? [];
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
    options: FeatureLayerQueryAllOptions = {},
  ): AsyncGenerator<unknown[], void, undefined> {
    const { pageSize: requestedPageSize, maxPages: requestedMaxPages, ...queryOptions } = options;
    const pageSize =
      typeof requestedPageSize === "number" && Number.isFinite(requestedPageSize)
        ? Math.max(1, Math.trunc(requestedPageSize))
        : 2000;
    const maxPages =
      typeof requestedMaxPages === "number" && Number.isFinite(requestedMaxPages)
        ? Math.max(1, Math.trunc(requestedMaxPages))
        : 100;

    for (let page = 0; page < maxPages; page += 1) {
      const response = await this.queryFeatures({
        ...queryOptions,
        extraParams: {
          ...(queryOptions.extraParams ?? {}),
          resultOffset: page * pageSize,
          resultRecordCount: pageSize,
        },
      });

      const pageFeatures = extractFeatures(response) ?? [];
      if (pageFeatures.length === 0) {
        break;
      }

      yield pageFeatures;
      if (pageFeatures.length < pageSize) {
        break;
      }
    }
  }

  public async queryObjectIds(options: FeatureLayerQueryCountOptions = {}): Promise<number[]> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      returnGeometry: false,
      method: options.method,
      extraParams: {
        ...options.extraParams,
        returnIdsOnly: true,
      },
    });

    if (isRecord(response) && Array.isArray(response.objectIds)) {
      return response.objectIds
        .map((value) => Number(value))
        .filter((value) => Number.isFinite(value));
    }

    const features = extractFeatures(response);
    if (!features) {
      return [];
    }

    return features
      .map((feature) => extractObjectId(feature))
      .filter((value): value is number => value !== undefined);
  }

  public async queryFeatureCount(options: FeatureLayerQueryCountOptions = {}): Promise<number> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      returnGeometry: false,
      method: options.method,
      extraParams: {
        ...options.extraParams,
        returnCountOnly: true,
      },
    });

    if (isRecord(response) && typeof response.count === "number") {
      return Number.isFinite(response.count) ? response.count : 0;
    }

    const features = extractFeatures(response);
    return features?.length ?? 0;
  }

  public async queryExtent(
    options: FeatureLayerQueryCountOptions = {},
  ): Promise<FeatureLayerQueryExtentResult> {
    const response = await this.client.queryFeatures({
      serviceId: this.serviceId,
      layerId: this.layerId,
      where: options.where ?? this.definitionExpression,
      returnGeometry: false,
      method: options.method,
      extraParams: {
        ...options.extraParams,
        returnExtentOnly: true,
      },
    });

    if (!isRecord(response)) {
      return { extent: null };
    }

    const count =
      typeof response.count === "number" && Number.isFinite(response.count) ? response.count : undefined;
    return {
      extent: response.extent ?? null,
      count,
    };
  }

  public async applyEdits(options: FeatureLayerEditsOptions): Promise<unknown> {
    const result = await this.client.applyEdits({
      serviceId: this.serviceId,
      layerId: this.layerId,
      adds: options.adds,
      updates: options.updates,
      deletes: options.deletes,
      rollbackOnFailure: options.rollbackOnFailure,
    });
    this.eventBus.emit("feature-layer.edits", { result, layerId: this.id }, this);
    return result;
  }

  public queryRelatedFeatures(options: FeatureLayerQueryRelatedFeaturesOptions): Promise<unknown> {
    return this.client.queryRelatedRecords({
      serviceId: this.serviceId,
      layerId: this.layerId,
      relationshipId: options.relationshipId,
      objectIds: options.objectIds,
      where: options.where ?? this.definitionExpression,
      outFields: options.outFields ?? this.outFields,
      returnGeometry: options.returnGeometry,
      method: options.method,
      extraParams: options.extraParams,
    });
  }

  public queryRelatedRecords(options: FeatureLayerQueryRelatedFeaturesOptions): Promise<unknown> {
    return this.queryRelatedFeatures(options);
  }

  public queryAttachments(options: FeatureLayerQueryAttachmentsOptions = {}): Promise<unknown> {
    return this.client.request({
      method: options.method ?? "GET",
      path: `/rest/services/${encodeURIComponent(this.serviceId)}/FeatureServer/${this.layerId}/queryAttachments`,
      responseFormat: options.responseFormat ?? "json",
      query: {
        ...(options.objectIds === undefined
          ? {}
          : {
              objectIds: Array.isArray(options.objectIds)
                ? options.objectIds.join(",")
                : options.objectIds,
            }),
        ...(options.where === undefined ? {} : { where: options.where }),
        ...(options.extraParams ?? {}),
      },
    });
  }

  public listAttachments(options: FeatureLayerListAttachmentsOptions): Promise<unknown> {
    return this.client.request({
      method: "GET",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${options.objectId}/attachments`,
      responseFormat: options.responseFormat ?? "json",
      query: options.extraParams,
    });
  }

  public deleteAttachments(options: FeatureLayerDeleteAttachmentsOptions): Promise<unknown> {
    const params = new URLSearchParams();
    params.set("f", options.responseFormat ?? "json");
    params.set(
      "attachmentIds",
      Array.isArray(options.attachmentIds)
        ? options.attachmentIds.join(",")
        : String(options.attachmentIds),
    );
    if (options.extraParams) {
      for (const [key, value] of Object.entries(options.extraParams)) {
        params.set(key, String(value));
      }
    }

    return this.client.request({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${options.objectId}/deleteAttachments`,
      headers: {
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params.toString(),
    });
  }

  public addAttachment(options: FeatureLayerAddAttachmentOptions): Promise<unknown> {
    enforceAttachmentSizeLimit(options.attachment, options.maxAttachmentBytes ?? this.maxAttachmentBytes);
    const form = buildAttachmentFormData(options);
    return this.client.request({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${options.objectId}/addAttachment`,
      responseFormat: options.responseFormat ?? "json",
      query: options.extraParams,
      body: form,
    });
  }

  public updateAttachment(options: FeatureLayerUpdateAttachmentOptions): Promise<unknown> {
    enforceAttachmentSizeLimit(options.attachment, options.maxAttachmentBytes ?? this.maxAttachmentBytes);
    const form = buildAttachmentFormData(options);
    form.set("attachmentId", String(options.attachmentId));
    return this.client.request({
      method: "POST",
      path:
        `/rest/services/${encodeURIComponent(this.serviceId)}` +
        `/FeatureServer/${this.layerId}/${options.objectId}/updateAttachment`,
      responseFormat: options.responseFormat ?? "json",
      query: options.extraParams,
      body: form,
    });
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

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function normalizeOpacity(opacity: number): number {
  if (!Number.isFinite(opacity)) {
    return 1;
  }
  return Math.min(Math.max(opacity, 0), 1);
}

function normalizeScale(scale: number | undefined): number {
  if (scale === undefined || !Number.isFinite(scale)) {
    return 0;
  }
  return Math.max(0, Math.trunc(scale));
}

function extractFeatures(value: unknown): unknown[] | undefined {
  if (!isRecord(value)) {
    return undefined;
  }
  if (!Array.isArray(value.features)) {
    return undefined;
  }
  return value.features;
}

function extractObjectId(feature: unknown): number | undefined {
  if (!isRecord(feature)) {
    return undefined;
  }

  const attributes = feature.attributes;
  if (!isRecord(attributes)) {
    return undefined;
  }

  for (const key of ["objectid", "OBJECTID", "id"]) {
    const raw = attributes[key];
    const parsed = Number(raw);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return undefined;
}

function extractFieldDefinitions(metadata: unknown): Record<string, unknown>[] {
  if (!isRecord(metadata)) {
    return [];
  }

  const fields = metadata.fields;
  if (!Array.isArray(fields)) {
    return [];
  }

  const records: Record<string, unknown>[] = [];
  for (const field of fields) {
    if (!isRecord(field)) {
      continue;
    }
    records.push({ ...field });
  }
  return records;
}

function buildAttachmentFormData(options: {
  attachment: FeatureLayerAttachmentData;
  name?: string;
  contentType?: string;
}): FormData {
  const form = new FormData();
  if (options.name) {
    form.set("name", options.name);
  }

  const attachmentBlob = normalizeAttachmentData(options.attachment, options.contentType);
  const attachmentName = resolveAttachmentName(options.attachment, options.name);
  form.set("attachment", attachmentBlob, attachmentName);
  return form;
}

function normalizeAttachmentSizeLimit(maxAttachmentBytes: number | undefined): number {
  if (typeof maxAttachmentBytes !== "number" || !Number.isFinite(maxAttachmentBytes)) {
    return DEFAULT_MAX_ATTACHMENT_BYTES;
  }
  return Math.max(1, Math.trunc(maxAttachmentBytes));
}

function enforceAttachmentSizeLimit(attachment: FeatureLayerAttachmentData, maxAttachmentBytes: number): void {
  const sizeBytes = estimateAttachmentSizeBytes(attachment);
  if (sizeBytes <= maxAttachmentBytes) {
    return;
  }
  throw new Error(
    `Attachment payload exceeds maxAttachmentBytes (${sizeBytes} > ${maxAttachmentBytes}).`,
  );
}

function estimateAttachmentSizeBytes(attachment: FeatureLayerAttachmentData): number {
  if (attachment instanceof Blob) {
    return attachment.size;
  }

  if (typeof attachment === "string") {
    return new TextEncoder().encode(attachment).byteLength;
  }

  if (attachment instanceof ArrayBuffer) {
    return attachment.byteLength;
  }

  return attachment.byteLength;
}

function resolveAttachmentName(
  attachment: FeatureLayerAttachmentData,
  explicitName?: string,
): string {
  if (explicitName && explicitName.trim().length > 0) {
    return explicitName.trim();
  }

  if (isRecord(attachment)) {
    const inferredName = attachment.name;
    if (typeof inferredName === "string" && inferredName.trim().length > 0) {
      return inferredName.trim();
    }
  }

  return "attachment.bin";
}

function buildTimeParam(
  timeExtent: { start: Date; end: Date } | undefined,
  extraParams?: Record<string, string | number | boolean>,
): string | undefined {
  if (!timeExtent) {
    return undefined;
  }
  if (extraParams && "time" in extraParams) {
    return undefined;
  }
  return `${timeExtent.start.getTime()},${timeExtent.end.getTime()}`;
}

function normalizeAttachmentData(
  attachment: FeatureLayerAttachmentData,
  contentType?: string,
): Blob {
  if (attachment instanceof Blob) {
    return attachment;
  }

  if (typeof attachment === "string") {
    return new Blob([attachment], {
      type: contentType ?? "text/plain",
    });
  }

  if (attachment instanceof ArrayBuffer) {
    return new Blob([attachment], {
      type: contentType ?? "application/octet-stream",
    });
  }

  if (ArrayBuffer.isView(attachment)) {
    const source = new Uint8Array(attachment.buffer, attachment.byteOffset, attachment.byteLength);
    const copy = Uint8Array.from(source);
    return new Blob([copy], {
      type: contentType ?? "application/octet-stream",
    });
  }

  throw new Error("Unsupported attachment payload type.");
}
