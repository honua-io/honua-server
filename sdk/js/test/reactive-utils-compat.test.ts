import { describe, expect, it } from "vitest";

import { reactiveUtils, watch, when, whenOnce } from "../src/index.js";

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

describe("reactiveUtils compat", () => {
  it("watches value changes with initial callback", async () => {
    let value = 0;
    const seen: number[] = [];

    const handle = watch(
      () => value,
      (next) => {
        seen.push(next);
      },
      { initial: true, intervalMs: 1 },
    );

    await sleep(5);
    value = 1;
    await sleep(5);
    value = 1;
    await sleep(5);
    value = 2;
    await sleep(5);
    handle.remove();
    value = 3;
    await sleep(5);

    expect(seen).toEqual([0, 1, 2]);
  });

  it("detects in-place object/array mutations without reference changes", async () => {
    const state = {
      filters: ["all"],
    };
    const seen: string[] = [];

    const handle = watch(
      () => state.filters,
      (next) => {
        seen.push(next.join(","));
      },
      { initial: true, intervalMs: 1 },
    );

    await sleep(5);
    state.filters.push("open");
    await sleep(5);
    state.filters[0] = "active";
    await sleep(5);
    handle.remove();

    expect(seen).toEqual(["all", "all,open", "active,open"]);
  });

  it("fires when() on false->true transitions", async () => {
    let enabled = false;
    const seen: boolean[] = [];

    const handle = when(
      () => enabled,
      (next) => {
        seen.push(next);
      },
      { intervalMs: 1 },
    );

    await sleep(5);
    enabled = true;
    await sleep(5);
    enabled = true;
    await sleep(5);
    enabled = false;
    await sleep(5);
    enabled = true;
    await sleep(5);
    handle.remove();

    expect(seen).toEqual([true, true]);
  });

  it("fires when() initial callback once per truthy period", async () => {
    let enabled = true;
    const seen: boolean[] = [];

    const handle = when(
      () => enabled,
      (next) => {
        seen.push(next);
      },
      { initial: true, intervalMs: 1 },
    );

    await sleep(8);
    enabled = false;
    await sleep(5);
    enabled = true;
    await sleep(5);
    handle.remove();

    expect(seen).toEqual([true, true]);
  });

  it("resolves whenOnce and exposes object helper", async () => {
    let state = 0;

    const resolved = whenOnce(
      () => {
        if (state > 1) {
          return state;
        }
        return 0;
      },
      { intervalMs: 1 },
    );

    await sleep(5);
    state = 2;
    await expect(resolved).resolves.toBe(2);
    expect(reactiveUtils.watch).toBe(watch);
    expect(reactiveUtils.when).toBe(when);
    expect(reactiveUtils.whenOnce).toBe(whenOnce);
  });
});
