import { describe, expect, it } from "vitest";

import { BookmarksCompat, MapViewCompat } from "../src/index.js";

describe("BookmarksCompat", () => {
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
