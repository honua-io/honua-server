import type { HonuaClient } from "./client.js";
import type { HonuaQueryResponse, MapLayerQueryRequest, QueryFeaturesRequest } from "./types.js";

/** A single query item in a batch. */
export interface BatchQueryItem {
  /** When `"map-layer"`, calls `client.queryMapLayer()`; default is `"feature"`. */
  type?: "feature" | "map-layer";
  /** The query request payload. */
  request: QueryFeaturesRequest | MapLayerQueryRequest;
  /** Optional label to identify this query in the results. */
  label?: string;
}

/** The result of a single query within a batch. */
export interface BatchQueryResult {
  /** The label from the corresponding `BatchQueryItem`, if provided. */
  label?: string;
  /** The successful query response, or `undefined` if the query failed. */
  response?: HonuaQueryResponse;
  /** The error thrown by the query, or `undefined` if the query succeeded. */
  error?: Error;
}

/** Options for controlling batch query execution. */
export interface BatchQueryOptions {
  /** Maximum number of concurrent queries. Default: `5`. */
  concurrency?: number;
  /** An `AbortSignal` that, when aborted, skips all remaining unstarted queries. */
  signal?: AbortSignal;
}

/**
 * Execute multiple queries in parallel with bounded concurrency.
 *
 * Results are returned in the same order as the input `items`.
 * Each result contains either a `response` (on success) or an `error` (on failure).
 * If an `AbortSignal` is provided and aborted, remaining unstarted queries are
 * skipped and their results will have an `error` set to an `AbortError`.
 */
export async function batchQuery(
  client: HonuaClient,
  items: readonly BatchQueryItem[],
  options?: BatchQueryOptions,
): Promise<BatchQueryResult[]> {
  const concurrency = Math.max(1, options?.concurrency ?? 5);
  const signal = options?.signal;

  const results: BatchQueryResult[] = new Array(items.length);

  // Simple semaphore: track how many queries are currently in-flight.
  let running = 0;
  const queue: Array<() => void> = [];

  function release(): void {
    running--;
    const next = queue.shift();
    if (next) {
      next();
    }
  }

  function acquire(): Promise<void> {
    if (running < concurrency) {
      running++;
      return Promise.resolve();
    }
    return new Promise<void>((resolve) => {
      queue.push(() => {
        running++;
        resolve();
      });
    });
  }

  const promises: Promise<void>[] = [];

  for (let i = 0; i < items.length; i++) {
    const index = i;
    const item = items[index];

    const task = async (): Promise<void> => {
      // Check abort before acquiring a semaphore slot.
      if (signal?.aborted) {
        results[index] = {
          label: item.label,
          error: new DOMException("The operation was aborted.", "AbortError"),
        };
        return;
      }

      await acquire();

      // Check abort again after acquiring the slot.
      if (signal?.aborted) {
        release();
        results[index] = {
          label: item.label,
          error: new DOMException("The operation was aborted.", "AbortError"),
        };
        return;
      }

      try {
        const response =
          item.type === "map-layer"
            ? await client.queryMapLayer(item.request as MapLayerQueryRequest)
            : await client.queryFeatures(item.request as QueryFeaturesRequest);
        results[index] = { label: item.label, response };
      } catch (err) {
        results[index] = {
          label: item.label,
          error: err instanceof Error ? err : new Error(String(err)),
        };
      } finally {
        release();
      }
    };

    promises.push(task());
  }

  await Promise.all(promises);

  return results;
}
