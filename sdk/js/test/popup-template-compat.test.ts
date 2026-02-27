import { describe, expect, it } from "vitest";

import { CompatEventBus, PopupTemplateCompat } from "../src/index.js";

describe("PopupTemplateCompat", () => {
  it("supports when() and watch() for lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const eventTypes: string[] = [];
    eventBus.onAny((event) => {
      eventTypes.push(event.type);
    });

    const template = new PopupTemplateCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = template.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = template.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackTemplate: PopupTemplateCompat | undefined;
    const resolved = await template.when((readyTemplate) => {
      callbackTemplate = readyTemplate;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };

    await template.load();

    expect(resolved).toBe(template);
    expect(callbackTemplate).toBe(template);
    expect(template.loaded).toBe(true);
    expect(template.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(eventTypes).toContain("popup-template.loading");
    expect(eventTypes).toContain("popup-template.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("stores template options and defensively copies option arrays", () => {
    const eventBus = new CompatEventBus();
    const fieldInfos = [{ fieldName: "OBJECTID" }];
    const actions = [{ id: "zoom-to" }];
    const expressionInfos = [{ name: "calc" }];
    const outFields = ["OBJECTID", "NAME"];

    const template = new PopupTemplateCompat({
      title: "Parcel",
      content: "Details",
      fieldInfos,
      actions,
      expressionInfos,
      outFields,
      eventBus,
    });

    expect(template.eventBus).toBe(eventBus);
    expect(template.title).toBe("Parcel");
    expect(template.content).toBe("Details");
    expect(template.fieldInfos).toEqual(fieldInfos);
    expect(template.actions).toEqual(actions);
    expect(template.expressionInfos).toEqual(expressionInfos);
    expect(template.outFields).toEqual(outFields);
    expect(template.fieldInfos).not.toBe(fieldInfos);
    expect(template.actions).not.toBe(actions);
    expect(template.expressionInfos).not.toBe(expressionInfos);
    expect(template.outFields).not.toBe(outFields);
  });

  it("updates provided properties and emits popup-template.updated", () => {
    const eventBus = new CompatEventBus();
    const events: unknown[] = [];
    const outFieldsValues: unknown[] = [];
    eventBus.on("popup-template.updated", (event) => {
      events.push(event);
    });

    const template = new PopupTemplateCompat({
      title: "Initial",
      content: "Before",
      outFields: ["OBJECTID"],
      eventBus,
    });
    const outFieldsHandle = template.watch("outFields", (value) => {
      outFieldsValues.push(value);
    });

    template.update({
      content: "After",
      outFields: ["NAME"],
    });
    outFieldsHandle.remove();
    const watchSnapshot = {
      outFields: outFieldsValues.length,
    };

    template.update({
      outFields: ["UPDATED_AGAIN"],
    });

    expect(template.title).toBe("Initial");
    expect(template.content).toBe("After");
    expect(template.outFields).toEqual(["UPDATED_AGAIN"]);
    expect(events).toHaveLength(2);
    expect(events[0]).toMatchObject({
      type: "popup-template.updated",
      payload: undefined,
      source: template,
    });
    expect(outFieldsValues).toEqual([["NAME"]]);
    expect(outFieldsValues).toHaveLength(watchSnapshot.outFields);
  });

  it("clones with equivalent values and shared event bus", () => {
    const eventBus = new CompatEventBus();
    const template = new PopupTemplateCompat({
      title: "Title",
      content: "Body",
      fieldInfos: [{ fieldName: "OBJECTID" }],
      actions: [{ id: "open" }],
      expressionInfos: [{ name: "expr" }],
      outFields: ["OBJECTID"],
      eventBus,
    });

    const cloned = template.clone();

    expect(cloned).not.toBe(template);
    expect(cloned.eventBus).toBe(eventBus);
    expect(cloned.title).toBe(template.title);
    expect(cloned.content).toBe(template.content);
    expect(cloned.fieldInfos).toEqual(template.fieldInfos);
    expect(cloned.actions).toEqual(template.actions);
    expect(cloned.expressionInfos).toEqual(template.expressionInfos);
    expect(cloned.outFields).toEqual(template.outFields);
    expect(cloned.fieldInfos).not.toBe(template.fieldInfos);
    expect(cloned.actions).not.toBe(template.actions);
    expect(cloned.expressionInfos).not.toBe(template.expressionInfos);
    expect(cloned.outFields).not.toBe(template.outFields);
  });
});
