window.maplibreInterop = (() => {
  const maps = new Map();

  const emptyStyle = {
    version: 8,
    sources: {},
    layers: []
  };

  const toStyleObject = (stylePayload) => {
    if (!stylePayload) {
      return emptyStyle;
    }

    if (typeof stylePayload === "string") {
      try {
        return JSON.parse(stylePayload);
      } catch (error) {
        console.error("Failed to parse MapLibre style JSON", error);
        return emptyStyle;
      }
    }

    return stylePayload;
  };

  const toBounds = (bounds) => {
    if (!Array.isArray(bounds) || bounds.length !== 4) {
      return null;
    }

    const [west, south, east, north] = bounds.map((value) => Number(value));
    if ([west, south, east, north].some((value) => Number.isNaN(value))) {
      return null;
    }

    return [
      [west, south],
      [east, north]
    ];
  };

  const applyView = (map, options) => {
    if (!options) {
      return;
    }

    const bounds = toBounds(options.bounds);
    if (bounds) {
      map.fitBounds(bounds, { padding: 32, duration: 0, maxZoom: options.maxZoom ?? 16 });
      return;
    }

    if (Array.isArray(options.center) && options.center.length === 2) {
      const center = [Number(options.center[0]), Number(options.center[1])];
      if (!center.some((value) => Number.isNaN(value))) {
        map.setCenter(center);
      }
    }

    if (typeof options.zoom === "number" && !Number.isNaN(options.zoom)) {
      map.setZoom(options.zoom);
    }
  };

  const buildPopupContent = (properties) => {
    const container = document.createElement("div");
    container.className = "maplibre-popup";

    const entries = Object.entries(properties ?? {});
    if (entries.length === 0) {
      const empty = document.createElement("div");
      empty.className = "maplibre-popup-empty";
      empty.textContent = "No attributes";
      container.appendChild(empty);
      return container;
    }

    for (const [key, value] of entries) {
      const row = document.createElement("div");
      row.className = "maplibre-popup-row";

      const label = document.createElement("span");
      label.className = "maplibre-popup-key";
      label.textContent = String(key);

      const display = document.createElement("span");
      display.className = "maplibre-popup-value";
      display.textContent = value === null || value === undefined ? "" : String(value);

      row.appendChild(label);
      row.appendChild(display);
      container.appendChild(row);
    }

    return container;
  };

  const clearPopup = (entry) => {
    if (entry.popup) {
      entry.popup.remove();
      entry.popup = null;
    }
  };

  const createMap = (containerId, stylePayload, options, dotnetRef) => {
    if (!containerId) {
      return;
    }

    removeMap(containerId);

    const style = toStyleObject(stylePayload);
    const map = new maplibregl.Map({
      container: containerId,
      style: style,
      center: [0, 0],
      zoom: 2,
      attributionControl: true
    });

    map.addControl(new maplibregl.NavigationControl(), "top-right");

    const entry = {
      map,
      popup: null,
      dotnetRef: dotnetRef || null
    };

    entry.onClick = (event) => {
      const features = map.queryRenderedFeatures(event.point);
      if (!features || features.length === 0) {
        clearPopup(entry);
        if (entry.dotnetRef) {
          entry.dotnetRef.invokeMethodAsync("OnFeatureSelected", null);
        }
        return;
      }

      const feature = features[0];
      const properties = feature?.properties || {};
      const popupContent = buildPopupContent(properties);

      clearPopup(entry);
      entry.popup = new maplibregl.Popup({ closeButton: true, closeOnClick: true, maxWidth: "320px" })
        .setLngLat(event.lngLat)
        .setDOMContent(popupContent)
        .addTo(map);

      if (entry.dotnetRef) {
        entry.dotnetRef.invokeMethodAsync("OnFeatureSelected", JSON.stringify(properties));
      }
    };

    entry.onError = (event) => {
      const message = event?.error?.message || "MapLibre failed to load the map.";
      console.error("MapLibre error", event);
      if (entry.dotnetRef) {
        entry.dotnetRef.invokeMethodAsync("OnMapError", message);
      }
    };

    entry.onLoad = () => {
      applyView(map, options);
      if (entry.dotnetRef) {
        entry.dotnetRef.invokeMethodAsync("OnMapLoaded");
      }
    };

    map.on("click", entry.onClick);
    map.on("error", entry.onError);
    map.on("load", entry.onLoad);

    maps.set(containerId, entry);
  };

  const updateStyle = (containerId, stylePayload, options) => {
    const entry = maps.get(containerId);
    if (!entry) {
      return;
    }

    const style = toStyleObject(stylePayload);
    entry.map.setStyle(style);
    entry.map.once("styledata", () => {
      applyView(entry.map, options);
    });
  };

  const fitBounds = (containerId, bounds) => {
    const entry = maps.get(containerId);
    if (!entry) {
      return;
    }

    const parsed = toBounds(bounds);
    if (!parsed) {
      return;
    }

    entry.map.fitBounds(parsed, { padding: 32, duration: 0, maxZoom: 16 });
  };

  const removeMap = (containerId) => {
    const entry = maps.get(containerId);
    if (!entry) {
      return;
    }

    clearPopup(entry);
    entry.map.off("click", entry.onClick);
    entry.map.off("error", entry.onError);
    entry.map.off("load", entry.onLoad);
    entry.map.remove();
    maps.delete(containerId);
  };

  const triggerFeature = (containerId, properties) => {
    const entry = maps.get(containerId);
    if (!entry || !entry.dotnetRef) {
      return;
    }

    const payload = properties ? JSON.stringify(properties) : null;
    entry.dotnetRef.invokeMethodAsync("OnFeatureSelected", payload);
  };

  const getState = (containerId) => {
    const entry = maps.get(containerId);
    if (!entry) {
      return null;
    }

    const center = entry.map.getCenter();
    return {
      center: [center.lng, center.lat],
      zoom: entry.map.getZoom()
    };
  };

  return {
    createMap,
    updateStyle,
    fitBounds,
    removeMap,
    triggerFeature,
    getState
  };
})();
