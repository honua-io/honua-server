import { describe, expect, it } from "vitest";

import { CompatEventBus, EditorCompat } from "../src/index.js";

describe("EditorCompat", () => {
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
