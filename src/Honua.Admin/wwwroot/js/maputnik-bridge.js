window.maputnikBridge = (() => {
  const STORAGE_NAMESPACE = "maputnik";
  const STYLE_PREFIX = `${STORAGE_NAMESPACE}:style:`;
  const LATEST_KEY = `${STORAGE_NAMESPACE}:latest_style`;

  let frame = null;
  let dotnetRef = null;
  let storageHandler = null;
  let loadHandler = null;

  const toStyleObject = (stylePayload) => {
    if (!stylePayload) {
      return null;
    }

    if (typeof stylePayload === "string") {
      try {
        return JSON.parse(stylePayload);
      } catch (error) {
        console.error("Failed to parse Maputnik style JSON", error);
        return null;
      }
    }

    return stylePayload;
  };

  const readLatestStyle = () => {
    try {
      const latestId = window.localStorage.getItem(LATEST_KEY);
      if (!latestId) {
        return null;
      }

      const raw = window.localStorage.getItem(`${STYLE_PREFIX}${latestId}`);
      if (!raw) {
        return null;
      }

      return JSON.parse(raw);
    } catch (error) {
      console.warn("Unable to read Maputnik style from storage", error);
      return null;
    }
  };

  const notifyStyleChanged = () => {
    if (!dotnetRef) {
      return;
    }

    const style = readLatestStyle();
    if (!style) {
      return;
    }

    dotnetRef.invokeMethodAsync("OnStyleChanged", style);
  };

  const handleStorageEvent = (event) => {
    if (!event || !event.key) {
      return;
    }

    if (event.key === LATEST_KEY || event.key.startsWith(STYLE_PREFIX)) {
      notifyStyleChanged();
    }
  };

  const init = (frameElement, dotnetReference) => {
    frame = frameElement;
    dotnetRef = dotnetReference;

    if (storageHandler) {
      window.removeEventListener("storage", storageHandler);
    }

    storageHandler = handleStorageEvent;
    window.addEventListener("storage", storageHandler);

    if (frame) {
      if (loadHandler) {
        frame.removeEventListener("load", loadHandler);
      }

      loadHandler = () => {
        notifyStyleChanged();
      };

      frame.addEventListener("load", loadHandler);
    }
  };

  const loadStyle = (stylePayload, options) => {
    const style = toStyleObject(stylePayload);
    if (!style) {
      return;
    }

    const desiredId = options?.styleId || style.id || `honua-style-${Date.now()}`;
    if (!style.id) {
      style.id = desiredId;
    }

    try {
      window.localStorage.setItem(`${STYLE_PREFIX}${style.id}`, JSON.stringify(style));
      window.localStorage.setItem(LATEST_KEY, style.id);
    } catch (error) {
      console.error("Failed to store Maputnik style locally", error);
    }

    if (frame) {
      const baseSrc = frame.getAttribute("data-maputnik-src") || frame.getAttribute("src") || "maputnik/index.html";
      const cacheBust = Date.now();
      const url = new URL(baseSrc, window.location.href);
      url.searchParams.set("cache", String(cacheBust));
      frame.setAttribute("src", url.toString());
    }

    notifyStyleChanged();
  };

  const getStyle = () => readLatestStyle();

  const dispose = () => {
    if (storageHandler) {
      window.removeEventListener("storage", storageHandler);
    }

    if (frame && loadHandler) {
      frame.removeEventListener("load", loadHandler);
    }

    storageHandler = null;
    loadHandler = null;
    frame = null;
    dotnetRef = null;
  };

  return {
    init,
    loadStyle,
    getStyle,
    dispose
  };
})();
