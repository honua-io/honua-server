import { describe, expect, it } from "vitest";

import { CompatEventBus, PrintCompat } from "../src/index.js";

describe("PrintCompat", () => {
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
