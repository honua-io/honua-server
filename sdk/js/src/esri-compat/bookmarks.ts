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

export type BookmarksLoadStatusCompat = "not-loaded" | "loading" | "loaded";

export interface BookmarksHandleCompat {
  remove(): void;
}

export class BookmarksCompat {
  public readonly view: unknown;
  public readonly container: unknown;
  public readonly eventBus: CompatEventBus;
  public loaded: boolean;
  public loadStatus: BookmarksLoadStatusCompat;
  public bookmarks: BookmarkCompatItem[];
  public activeBookmark: BookmarkCompatItem | undefined;
  private readonly watchListeners: Map<string, Set<(value: unknown) => void>>;

  public constructor(options: BookmarksCompatOptions = {}) {
    this.view = options.view;
    this.container = options.container;
    this.eventBus = options.eventBus ?? resolveCompatEventBus(options.view) ?? new CompatEventBus();
    this.loaded = false;
    this.loadStatus = "not-loaded";
    this.bookmarks = options.bookmarks ? [...options.bookmarks] : [];
    this.activeBookmark = undefined;
    this.watchListeners = new Map();
  }

  public async load(): Promise<BookmarksCompat> {
    if (this.loaded) {
      return this;
    }

    this.loadStatus = "loading";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("bookmarks.loading", undefined, this);
    this.loaded = true;
    this.notifyWatchers("loaded", this.loaded);
    this.loadStatus = "loaded";
    this.notifyWatchers("loadStatus", this.loadStatus);
    this.eventBus.emit("bookmarks.loaded", { bookmarkCount: this.bookmarks.length }, this);
    return this;
  }

  public async when(callback?: (widget: BookmarksCompat) => void): Promise<BookmarksCompat> {
    const widget = await this.load();
    if (callback) {
      callback(widget);
    }
    return widget;
  }

  public watch(propertyName: string, listener: (value: unknown) => void): BookmarksHandleCompat {
    let listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      listeners = new Set();
      this.watchListeners.set(propertyName, listeners);
    }

    listeners.add(listener);
    return {
      remove: () => {
        listeners?.delete(listener);
      },
    };
  }

  public add(bookmark: BookmarkCompatItem): void {
    this.bookmarks.push(bookmark);
    this.notifyWatchers("bookmarks", this.bookmarks);
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
    this.notifyWatchers("bookmarks", this.bookmarks);
    if (this.activeBookmark === removed) {
      this.activeBookmark = undefined;
      this.notifyWatchers("activeBookmark", this.activeBookmark);
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
    this.notifyWatchers("activeBookmark", this.activeBookmark);
    const target = bookmark.viewpoint ?? bookmark.target;
    if (target !== undefined && isGoToProvider(this.view)) {
      await this.view.goTo(target);
    }
    this.eventBus.emit("bookmarks.go-to", { bookmark }, this);
    return bookmark;
  }

  public destroy(): void {
    this.watchListeners.clear();
  }

  private notifyWatchers(propertyName: string, value: unknown): void {
    const listeners = this.watchListeners.get(propertyName);
    if (!listeners) {
      return;
    }

    for (const listener of listeners) {
      listener(value);
    }
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
