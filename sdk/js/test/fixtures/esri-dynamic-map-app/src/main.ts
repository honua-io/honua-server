const loadMapCtor = () => import("@arcgis/core/Map").then((moduleItem) => moduleItem.default);

async function createMap() {
  const MapCtor = await loadMapCtor();
  return new MapCtor({
    basemap: "streets-vector",
  });
}

void createMap();
