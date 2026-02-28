export interface ReactiveUtilsHandleCompat {
  remove(): void;
}

export interface ReactiveUtilsWatchOptionsCompat {
  initial?: boolean;
  once?: boolean;
  intervalMs?: number;
  equals?: (current: unknown, previous: unknown) => boolean;
}

export interface ReactiveUtilsWhenOptionsCompat {
  initial?: boolean;
  once?: boolean;
  intervalMs?: number;
}

const DEFAULT_INTERVAL_MS = 16;
const MAX_COMPARISON_DEPTH = 8;

export function watch<TValue>(
  getter: () => TValue,
  callback: (value: TValue) => void,
  options: ReactiveUtilsWatchOptionsCompat = {},
): ReactiveUtilsHandleCompat {
  let active = true;
  let hasValue = false;
  let previousValue: TValue | undefined;
  let previousComparable: unknown;
  const intervalMs = normalizeInterval(options.intervalMs);

  const evaluate = (): void => {
    if (!active) {
      return;
    }

    let currentValue: TValue;
    try {
      currentValue = getter();
    } catch {
      return;
    }
    const currentComparable = createComparableValue(currentValue);

    if (!hasValue) {
      hasValue = true;
      previousValue = currentValue;
      previousComparable = currentComparable;
      if (options.initial) {
        callback(currentValue);
        if (options.once) {
          remove();
        }
      }
      return;
    }

    if (valuesEqual(currentValue, previousValue, currentComparable, previousComparable, options.equals)) {
      return;
    }

    previousValue = currentValue;
    previousComparable = currentComparable;
    callback(currentValue);
    if (options.once) {
      remove();
    }
  };

  const timer = setInterval(evaluate, intervalMs);
  evaluate();

  const remove = (): void => {
    if (!active) {
      return;
    }
    active = false;
    clearInterval(timer);
  };

  return { remove };
}

export function when<TValue>(
  getter: () => TValue,
  callback: (value: TValue) => void,
  options: ReactiveUtilsWhenOptionsCompat = {},
): ReactiveUtilsHandleCompat {
  let active = true;
  let seenTruthy = false;
  let hasEvaluated = false;
  const intervalMs = normalizeInterval(options.intervalMs);
  const once = options.once ?? false;
  const initial = options.initial ?? true;

  const evaluate = (): void => {
    if (!active) {
      return;
    }

    let value: TValue;
    try {
      value = getter();
    } catch {
      return;
    }

    const truthy = Boolean(value);
    if (!truthy) {
      seenTruthy = false;
      hasEvaluated = true;
      return;
    }

    const isFirstEvaluation = !hasEvaluated;
    const shouldNotify = !seenTruthy && (!isFirstEvaluation || initial);
    if (shouldNotify) {
      callback(value);
      if (once) {
        remove();
        return;
      }
    }

    seenTruthy = true;
    hasEvaluated = true;
  };

  const timer = setInterval(evaluate, intervalMs);
  evaluate();

  const remove = (): void => {
    if (!active) {
      return;
    }
    active = false;
    clearInterval(timer);
  };

  return { remove };
}

export function whenOnce<TValue>(
  getter: () => TValue,
  options: Omit<ReactiveUtilsWhenOptionsCompat, "once"> = {},
): Promise<TValue> {
  return new Promise((resolve) => {
    const handle = when(
      getter,
      (value) => {
        handle.remove();
        resolve(value);
      },
      {
        ...options,
        initial: true,
        once: true,
      },
    );
  });
}

export const reactiveUtils = {
  watch,
  when,
  whenOnce,
};

function normalizeInterval(intervalMs: number | undefined): number {
  if (typeof intervalMs !== "number" || !Number.isFinite(intervalMs) || intervalMs < 1) {
    return DEFAULT_INTERVAL_MS;
  }
  return Math.max(1, Math.trunc(intervalMs));
}

function valuesEqual<TValue>(
  current: TValue,
  previous: TValue | undefined,
  currentComparable: unknown,
  previousComparable: unknown,
  equals?: (current: unknown, previous: unknown) => boolean,
): boolean {
  if (equals) {
    return equals(current, previous);
  }
  return Object.is(currentComparable, previousComparable);
}

function createComparableValue(value: unknown): unknown {
  if (Array.isArray(value) || isPlainObject(value)) {
    return stableSerialize(value, 0, new WeakSet<object>());
  }
  return value;
}

function stableSerialize(
  value: unknown,
  depth: number,
  seen: WeakSet<object>,
): string {
  if (value === null) {
    return "null";
  }
  if (depth >= MAX_COMPARISON_DEPTH) {
    return '"[MaxDepth]"';
  }

  const valueType = typeof value;
  if (valueType === "string") {
    return JSON.stringify(value);
  }
  if (valueType === "number" || valueType === "boolean" || valueType === "bigint") {
    return String(value);
  }
  if (valueType === "undefined") {
    return "undefined";
  }
  if (valueType !== "object") {
    return valueType;
  }

  if (seen.has(value as object)) {
    return '"[Circular]"';
  }
  seen.add(value as object);

  if (Array.isArray(value)) {
    const serialized = `[${value.map((entry) => stableSerialize(entry, depth + 1, seen)).join(",")}]`;
    seen.delete(value as object);
    return serialized;
  }

  const entries = Object.entries(value as Record<string, unknown>)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, entryValue]) => `${JSON.stringify(key)}:${stableSerialize(entryValue, depth + 1, seen)}`);
  const serialized = `{${entries.join(",")}}`;
  seen.delete(value as object);
  return serialized;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}
