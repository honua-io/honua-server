import { afterEach, describe, expect, it, vi } from "vitest";

import { esriRequest } from "../src/index.js";

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("esriRequest compat", () => {
  it("executes JSON requests with query params", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await esriRequest("https://example.test/rest/services/demo", {
      query: {
        f: "json",
        where: "1=1",
      },
    });

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const calledUrl = (fetchMock.mock.calls as unknown[][])[0]?.[0];
    expect(String(calledUrl)).toContain("f=json");
    expect(result.data).toEqual({ ok: true });
    expect(result.status).toBe(200);
  });

  it("supports text response type", async () => {
    const fetchMock = vi.fn(async () => new Response("ok", { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await esriRequest<string>("https://example.test/ping", {
      responseType: "text",
    });

    expect(result.data).toBe("ok");
  });

  it("supports relative URLs when query params are provided", async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ ok: true }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    await esriRequest("/rest/services/demo/FeatureServer/0/query", {
      query: {
        f: "json",
        where: "1=1",
      },
    });

    const calledUrl = String((fetchMock.mock.calls as unknown[][])[0]?.[0]);
    expect(calledUrl.startsWith("/rest/services/demo/FeatureServer/0/query?")).toBe(true);
    expect(calledUrl).toContain("f=json");
    expect(calledUrl).toContain("where=1%3D1");
  });
});
