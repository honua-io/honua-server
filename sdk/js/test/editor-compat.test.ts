import { describe, expect, it } from "vitest";

import { CompatEventBus, EditorCompat } from "../src/index.js";

describe("EditorCompat", () => {
  it("supports when() and watch() lifecycle and workflow state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const editor = new EditorCompat({ eventBus });
    const loadStatusValues: unknown[] = [];
    const workflowValues: unknown[] = [];
    const loadStatusHandle = editor.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const workflowHandle = editor.watch("activeWorkflow", (value) => {
      workflowValues.push(value);
    });

    let callbackWidget: EditorCompat | undefined;
    const widget = await editor.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });
    editor.startUpdateWorkflowAtFeatureSelection();
    editor.stopWorkflow();

    loadStatusHandle.remove();
    workflowHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      workflow: workflowValues.length,
    };
    editor.startUpdateWorkflowAtFeatureSelection();

    expect(widget).toBe(editor);
    expect(callbackWidget).toBe(editor);
    expect(editor.loaded).toBe(true);
    expect(editor.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(workflowValues).toEqual(["update", undefined]);
    expect(seenTypes).toContain("editor.loading");
    expect(seenTypes).toContain("editor.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(workflowValues).toHaveLength(watchSnapshot.workflow);
  });

  it("starts and stops workflows while emitting lifecycle events", () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const feature = { objectId: 10 };
    const layer = { graphics: [feature] };
    const editor = new EditorCompat({
      eventBus,
      layerInfos: [{ layer, deleteEnabled: true }],
    });

    expect(editor.startCreateWorkflowAtFeatureTypeSelection(layer)).toBe(true);
    expect(editor.activeWorkflow).toBe("create");

    expect(editor.startUpdateWorkflowAtFeatureEdit(feature)).toBe(true);
    expect(editor.activeWorkflow).toBe("update");
    expect(editor.selectedFeature).toBe(feature);

    expect(editor.deleteFeatureFromWorkflow()).toBe(true);
    expect(layer.graphics).toEqual([]);

    editor.stopWorkflow();
    expect(editor.activeWorkflow).toBeUndefined();
    expect(seenTypes).toContain("editor.workflow-started");
    expect(seenTypes).toContain("editor.feature-selected");
    expect(seenTypes).toContain("editor.feature-deleted");
    expect(seenTypes).toContain("editor.workflow-stopped");
  });

  it("honors allowedWorkflows restrictions", () => {
    const editor = new EditorCompat({ allowedWorkflows: ["update"] });

    expect(editor.startCreateWorkflowAtFeatureTypeSelection()).toBe(false);
    expect(editor.startUpdateWorkflowAtFeatureSelection()).toBe(true);
    expect(editor.activeWorkflow).toBe("update");
  });
});
