import { describe, expect, it } from "vitest";

import { BookmarksCompat, CompatEventBus, MapViewCompat } from "../src/index.js";

describe("BookmarksCompat", () => {
  it("supports when() and watch() lifecycle state", async () => {
    const eventBus = new CompatEventBus();
    const seenTypes: string[] = [];
    eventBus.onAny((event) => {
      seenTypes.push(event.type);
    });

    const bookmarks = new BookmarksCompat({
      eventBus,
      bookmarks: [{ name: "Home" }],
    });

    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const loadStatusHandle = bookmarks.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = bookmarks.watch("loaded", (value) => {
      loadedValues.push(value);
    });

    let callbackWidget: BookmarksCompat | undefined;
    const widget = await bookmarks.when((resolvedWidget) => {
      callbackWidget = resolvedWidget;
    });

    loadStatusHandle.remove();
    loadedHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
    };
    await bookmarks.load();

    expect(widget).toBe(bookmarks);
    expect(callbackWidget).toBe(bookmarks);
    expect(bookmarks.loaded).toBe(true);
    expect(bookmarks.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(seenTypes).toContain("bookmarks.loading");
    expect(seenTypes).toContain("bookmarks.loaded");
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
  });

  it("adds/removes bookmarks and navigates to selected bookmark", async () => {
    const view = new MapViewCompat({ center: [0, 0], zoom: 2 });
    const bookmarks = new BookmarksCompat({
      view,
      bookmarks: [
        {
          name: "Home",
          target: {
            center: [0, 0],
            zoom: 2,
          },
        },
      ],
    });

    bookmarks.add({
      name: "Downtown",
      target: {
        center: [-157.8583, 21.3069],
        zoom: 12,
      },
    });
    expect(bookmarks.bookmarks).toHaveLength(2);

    const bookmark = await bookmarks.goTo("Downtown");
    expect(bookmark).toMatchObject({ name: "Downtown" });
    expect(bookmarks.activeBookmark).toMatchObject({ name: "Downtown" });
    expect(view.center).toEqual([-157.8583, 21.3069]);
    expect(view.zoom).toBe(12);

    expect(bookmarks.remove("Home")).toBe(true);
    expect(bookmarks.remove("Missing")).toBe(false);
    expect(bookmarks.bookmarks).toHaveLength(1);
  });
});
