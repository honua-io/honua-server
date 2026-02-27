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

export function watch<TValue>(
  getter: () => TValue,
  callback: (value: TValue) => void,
  options: ReactiveUtilsWatchOptionsCompat = {},
): ReactiveUtilsHandleCompat {
  let active = true;
  let hasValue = false;
  let previousValue: TValue | undefined;
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

    if (!hasValue) {
      hasValue = true;
      previousValue = currentValue;
      if (options.initial) {
        callback(currentValue);
        if (options.once) {
          remove();
        }
      }
      return;
    }

    if (valuesEqual(currentValue, previousValue, options.equals)) {
      return;
    }

    previousValue = currentValue;
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
  equals?: (current: unknown, previous: unknown) => boolean,
): boolean {
  if (equals) {
    return equals(current, previous);
  }
  return Object.is(current, previous);
}
