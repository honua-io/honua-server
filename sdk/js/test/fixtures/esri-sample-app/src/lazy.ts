export async function loadScene() {
  const module = await import("@arcgis/core/views/SceneView");
  return module.default;
}
