export interface CompatEvent<TPayload = unknown> {
  type: string;
  payload: TPayload;
  source: unknown;
  timestamp: number;
}

export type CompatEventListener<TPayload = unknown> = (event: CompatEvent<TPayload>) => void;

export interface CompatEventSubscription {
  remove(): void;
}

/** Well-known event payload types for the compat layer event bus. */
export interface CompatEventPayloads {
  "layer.visibility-changed": { layerId: string; serviceId?: string; sublayerId?: number; visible: boolean };
  "layer.opacity-changed": { layerId: string; serviceId?: string; sublayerId?: number; opacity: number };
  "feature-layer.loading": { serviceId: string; layerId: number; id: string };
  "feature-layer.loaded": { serviceId: string; layerId: number; id: string };
  "feature-layer.failed": { serviceId: string; layerId: number; id: string; error: unknown };
  "feature-layer.refreshed": { serviceId: string; layerId: number; id: string };
  "feature-layer.destroyed": { id: string };
  "feature-layer.edits": { result: unknown; layerId: string };
  "feature-layer.renderer-changed": { layerId: string };
  "feature-layer.popup-template-changed": { layerId: string };
  "feature-layer.labeling-changed": { layerId: string };
  "feature-layer.definition-expression-changed": { layerId: string; definitionExpression: string | undefined };
  "feature-layer.out-fields-changed": { layerId: string; outFields: string[] | undefined };
  "feature-layer.labels-visible-changed": { layerId: string; labelsVisible: boolean };
  "feature-layer.scale-range-changed": { layerId: string; minScale: number; maxScale: number };
  "feature-layer.legend-enabled-changed": { layerId: string; legendEnabled: boolean };
  "feature-layer.time-extent-change": { layerId: string; timeExtent: { start: Date; end: Date } | undefined };
}

type UntypedCompatEventListener = CompatEventListener<unknown>;

export class CompatEventBus {
  private readonly listenersByType: Map<string, Set<UntypedCompatEventListener>>;
  private readonly anyListeners: Set<UntypedCompatEventListener>;

  public constructor() {
    this.listenersByType = new Map();
    this.anyListeners = new Set();
  }

  public on<K extends keyof CompatEventPayloads>(
    type: K,
    listener: CompatEventListener<CompatEventPayloads[K]>,
  ): CompatEventSubscription;
  public on<TPayload = unknown>(type: string, listener: CompatEventListener<TPayload>): CompatEventSubscription;
  public on<TPayload = unknown>(type: string, listener: CompatEventListener<TPayload>): CompatEventSubscription {
    let listeners = this.listenersByType.get(type);
    if (!listeners) {
      listeners = new Set();
      this.listenersByType.set(type, listeners);
    }

    const untypedListener = listener as UntypedCompatEventListener;
    listeners.add(untypedListener);
    return {
      remove: () => {
        listeners?.delete(untypedListener);
      },
    };
  }

  public onAny<TPayload = unknown>(listener: CompatEventListener<TPayload>): CompatEventSubscription {
    const untypedListener = listener as UntypedCompatEventListener;
    this.anyListeners.add(untypedListener);
    return {
      remove: () => {
        this.anyListeners.delete(untypedListener);
      },
    };
  }

  public emit<TPayload = unknown>(type: string, payload: TPayload, source: unknown = undefined): CompatEvent<TPayload> {
    const event: CompatEvent<TPayload> = {
      type,
      payload,
      source,
      timestamp: Date.now(),
    };
    this.dispatchToTypeListeners(type, event);
    this.dispatchToAnyListeners(event);
    return event;
  }

  public clear(): void {
    this.listenersByType.clear();
    this.anyListeners.clear();
  }

  private dispatchToTypeListeners(type: string, event: CompatEvent<unknown>): void {
    const listeners = this.listenersByType.get(type);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      this.safeInvoke(listener, event);
    }
  }

  private dispatchToAnyListeners(event: CompatEvent<unknown>): void {
    for (const listener of this.anyListeners) {
      this.safeInvoke(listener, event);
    }
  }

  private safeInvoke(listener: UntypedCompatEventListener, event: CompatEvent<unknown>): void {
    try {
      listener(event);
    } catch {
      // Event listeners should not break compatibility flow.
    }
  }
}

export function resolveCompatEventBus(...candidates: readonly unknown[]): CompatEventBus | undefined {
  for (const candidate of candidates) {
    if (!isRecord(candidate)) {
      continue;
    }

    if (candidate.eventBus instanceof CompatEventBus) {
      return candidate.eventBus;
    }
  }

  return undefined;
}

export function safeInvokeCompatListener<TValue>(listener: (value: TValue) => void, value: TValue): void {
  try {
    listener(value);
  } catch {
    // Listener errors should not break compatibility flow.
  }
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
