import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export type EditorWorkflowCompat = "create" | "update";

export interface EditorLayerInfoCompat {
  layer?: unknown;
  enabled?: boolean;
  addEnabled?: boolean;
  updateEnabled?: boolean;
  deleteEnabled?: boolean;
  formTemplate?: unknown;
}

export interface EditorCompatOptions {
  view?: unknown;
  container?: unknown;
  eventBus?: CompatEventBus;
  layerInfos?: readonly EditorLayerInfoCompat[];
  allowedWorkflows?: readonly EditorWorkflowCompat[];
  supportingWidgetDefaults?: Record<string, unknown>;
}

export class EditorCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public layerInfos: EditorLayerInfoCompat[];
  public allowedWorkflows: EditorWorkflowCompat[];
  public supportingWidgetDefaults: Record<string, unknown>;
  public activeWorkflow: EditorWorkflowCompat | undefined;
  public selectedFeature: unknown;

  public constructor(options: EditorCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.layerInfos = [...(options.layerInfos ?? [])];
    this.allowedWorkflows = [...(options.allowedWorkflows ?? ["create", "update"])];
    this.supportingWidgetDefaults = { ...(options.supportingWidgetDefaults ?? {}) };
    this.activeWorkflow = undefined;
    this.selectedFeature = undefined;
  }

  public startCreateWorkflowAtFeatureTypeSelection(layer?: unknown): boolean {
    if (!this.allowedWorkflows.includes("create")) {
      return false;
    }

    this.activeWorkflow = "create";
    this.selectedFeature = undefined;
    this.eventBus.emit(
      "editor.workflow-started",
      {
        workflow: "create",
        layer,
      },
      this,
    );
    return true;
  }

  public startUpdateWorkflowAtFeatureSelection(): boolean {
    if (!this.allowedWorkflows.includes("update")) {
      return false;
    }

    this.activeWorkflow = "update";
    this.eventBus.emit(
      "editor.workflow-started",
      {
        workflow: "update",
      },
      this,
    );
    return true;
  }

  public startUpdateWorkflowAtFeatureEdit(feature: unknown): boolean {
    const started = this.startUpdateWorkflowAtFeatureSelection();
    if (!started) {
      return false;
    }

    this.selectedFeature = feature;
    this.eventBus.emit("editor.feature-selected", { feature }, this);
    return true;
  }

  public deleteFeatureFromWorkflow(feature?: unknown): boolean {
    const targetFeature = feature ?? this.selectedFeature;
    if (targetFeature === undefined) {
      return false;
    }

    let removed = false;
    for (const layerInfo of this.layerInfos) {
      if (layerInfo.deleteEnabled === false) {
        continue;
      }
      if (removeFeatureFromLayer(layerInfo.layer, targetFeature)) {
        removed = true;
      }
    }

    if (removed) {
      this.eventBus.emit("editor.feature-deleted", { feature: targetFeature }, this);
    }
    if (this.selectedFeature === targetFeature) {
      this.selectedFeature = undefined;
    }
    return removed;
  }

  public stopWorkflow(): void {
    if (!this.activeWorkflow) {
      return;
    }

    const workflow = this.activeWorkflow;
    this.activeWorkflow = undefined;
    this.selectedFeature = undefined;
    this.eventBus.emit("editor.workflow-stopped", { workflow }, this);
  }
}

interface EditorLayerLike {
  remove?(feature: unknown): unknown;
  graphics?: unknown[];
}

function removeFeatureFromLayer(layer: unknown, feature: unknown): boolean {
  if (!isEditorLayerLike(layer)) {
    return false;
  }

  if (typeof layer.remove === "function") {
    const beforeCount = Array.isArray(layer.graphics) ? layer.graphics.length : undefined;
    const result = layer.remove(feature);
    if (result !== undefined) {
      return true;
    }
    if (beforeCount !== undefined && Array.isArray(layer.graphics)) {
      return layer.graphics.length < beforeCount;
    }
    return false;
  }

  if (!Array.isArray(layer.graphics)) {
    return false;
  }

  const index = layer.graphics.findIndex((candidate) => candidate === feature);
  if (index < 0) {
    return false;
  }
  layer.graphics.splice(index, 1);
  return true;
}

function isEditorLayerLike(value: unknown): value is EditorLayerLike {
  return typeof value === "object" && value !== null;
}
