import { describe, expect, it } from "vitest";

import { CompatEventBus, PrintCompat } from "../src/index.js";

describe("PrintCompat", () => {
  it("supports when() and watch() lifecycle and last result", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const printer = new PrintCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const resultValues: unknown[] = [];
    const loadStatusHandle = printer.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const resultHandle = printer.watch("lastResult", (value) => {
      resultValues.push(value);
    });

    let callbackWidget: PrintCompat | undefined;
    const widget = await printer.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    await printer.execute({ title: "Export A" });

    loadStatusHandle.remove();
    resultHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      results: resultValues.length,
    };
    await printer.execute({ title: "Export B" });

    expect(widget).toBe(printer);
    expect(callbackWidget).toBe(printer);
    expect(printer.loaded).toBe(true);
    expect(printer.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(resultValues).toHaveLength(1);
    expect(resultValues[0]).toMatchObject({ title: "Export A" });
    expect(seenTypes).toContain("print.loading");
    expect(seenTypes).toContain("print.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(resultValues).toHaveLength(watchSnapshot.results);
  });

  it("executes print and returns deterministic export metadata", async () => {
    const printer = new PrintCompat({
      printServiceUrl: "https://example.test/print",
      templateOptions: {
        format: "png32",
        layout: "a3-landscape",
      },
    });

    const result = await printer.execute({
      title: "Parcels",
      dpi: 150,
    });

    expect(result).toMatchObject({
      title: "Parcels",
      format: "png32",
      layout: "a3-landscape",
      dpi: 150,
    });
    expect(result.url).toContain("title=Parcels");
    expect(result.url).toContain("format=png32");
  });

  it("emits template and execute lifecycle events", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const printer = new PrintCompat({ eventBus });
    printer.setTemplateOptions({ format: "jpg", layout: "map-only" });
    await printer.execute({ title: "Downtown" });

    expect(seenTypes).toContain("print.template-updated");
    expect(seenTypes).toContain("print.execute-started");
    expect(seenTypes).toContain("print.execute-completed");
  });
});
