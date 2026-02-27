async function createMap() {
  const MapCtor = (await import("@arcgis/core/Map")).default;
  return new MapCtor({
    basemap: "streets",
  });
}

void createMap();
