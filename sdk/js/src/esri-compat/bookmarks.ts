import { CompatEventBus, resolveCompatEventBus } from "./event-bus.js";

export interface BookmarkCompatItem {
  name: string;
  viewpoint?: unknown;
  target?: unknown;
}

export interface BookmarksCompatOptions {
  view?: unknown;
  container?: unknown;
  bookmarks?: readonly BookmarkCompatItem[];
  eventBus?: CompatEventBus;
}

export class BookmarksCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public bookmarks: BookmarkCompatItem[];
  public activeBookmark: BookmarkCompatItem | undefined;

  public constructor(options: BookmarksCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.bookmarks = options.bookmarks ? [...options.bookmarks] : [];
    this.activeBookmark = undefined;
  }

  public add(bookmark: BookmarkCompatItem): void {
    this.bookmarks.push(bookmark);
    this.eventBus.emit("bookmarks.updated", { bookmarkCount: this.bookmarks.length }, this);
  }

  public remove(nameOrBookmark: string | BookmarkCompatItem): boolean {
    const index =
      typeof nameOrBookmark === "string"
        ? this.bookmarks.findIndex((bookmark) => bookmark.name === nameOrBookmark)
        : this.bookmarks.indexOf(nameOrBookmark);
    if (index < 0) {
      return false;
    }

    const [removed] = this.bookmarks.splice(index, 1);
    if (this.activeBookmark === removed) {
      this.activeBookmark = undefined;
    }
    this.eventBus.emit("bookmarks.updated", { bookmarkCount: this.bookmarks.length }, this);
    return true;
  }

  public async goTo(nameOrBookmark: string | BookmarkCompatItem): Promise<BookmarkCompatItem | undefined> {
    const bookmark =
      typeof nameOrBookmark === "string"
        ? this.bookmarks.find((item) => item.name === nameOrBookmark)
        : nameOrBookmark;
    if (!bookmark) {
      return undefined;
    }

    this.activeBookmark = bookmark;
    const target = bookmark.viewpoint ?? bookmark.target;
    if (target !== undefined && isGoToProvider(this.view)) {
      await this.view.goTo(target);
    }
    this.eventBus.emit("bookmarks.go-to", { bookmark }, this);
    return bookmark;
  }
}

interface GoToProvider {
  goTo(target: unknown): Promise<unknown> | unknown;
}

function isGoToProvider(value: unknown): value is GoToProvider {
  return isRecord(value) && typeof value.goTo === "function";
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === "object" && value !== null;
}
