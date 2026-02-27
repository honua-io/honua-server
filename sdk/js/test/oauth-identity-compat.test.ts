import { afterEach, describe, expect, it } from "vitest";

import { identityManager, OAuthInfoCompat } from "../src/index.js";

afterEach(() => {
  identityManager.reset();
});

describe("OAuth/Identity compat", () => {
  it("stores OAuth info entries with lifecycle/watch behavior", async () => {
    const info = new OAuthInfoCompat({
      appId: "app-123",
      portalUrl: "https://portal.example.test",
      popup: true,
      expiration: 90,
    });
    const loadStatusValues: unknown[] = [];
    const loadedValues: unknown[] = [];
    const appIdValues: unknown[] = [];
    const loadStatusHandle = info.watch("loadStatus", (value) => {
      loadStatusValues.push(value);
    });
    const loadedHandle = info.watch("loaded", (value) => {
      loadedValues.push(value);
    });
    const appIdHandle = info.watch("appId", (value) => {
      appIdValues.push(value);
    });

    let callbackInfo: OAuthInfoCompat | undefined;
    const resolved = await info.when((readyInfo) => {
      callbackInfo = readyInfo;
    });
    info.update({ appId: "app-124" });

    loadStatusHandle.remove();
    loadedHandle.remove();
    appIdHandle.remove();
    const watchSnapshot = {
      loadStatus: loadStatusValues.length,
      loaded: loadedValues.length,
      appId: appIdValues.length,
    };

    await info.load();
    info.update({ appId: "app-125" });

    identityManager.registerOAuthInfos([info]);
    const stored = identityManager.oauthInfos;

    expect(resolved).toBe(info);
    expect(callbackInfo).toBe(info);
    expect(info.loaded).toBe(true);
    expect(info.loadStatus).toBe("loaded");
    expect(loadStatusValues).toEqual(["loading", "loaded"]);
    expect(loadedValues).toEqual([true]);
    expect(appIdValues).toEqual(["app-124"]);
    expect(loadStatusValues).toHaveLength(watchSnapshot.loadStatus);
    expect(loadedValues).toHaveLength(watchSnapshot.loaded);
    expect(appIdValues).toHaveLength(watchSnapshot.appId);
    expect(stored).toHaveLength(1);
    expect(stored[0]?.appId).toBe("app-125");
    expect(stored[0]?.portalUrl).toBe("https://portal.example.test");
  });

  it("registers and resolves credentials by server", async () => {
    identityManager.registerToken({
      server: "https://portal.example.test/sharing",
      token: "token-abc",
      userId: "user-1",
    });

    const credential = await identityManager.checkSignInStatus(
      "https://portal.example.test/sharing/rest",
    );

    expect(credential.token).toBe("token-abc");
    expect(credential.userId).toBe("user-1");
  });

  it("clears state on reset", () => {
    identityManager.registerOAuthInfos([{ appId: "app-1" }]);
    identityManager.registerToken({
      server: "https://portal.example.test/sharing",
      token: "token-1",
    });

    identityManager.reset();

    expect(identityManager.oauthInfos).toEqual([]);
    expect(identityManager.credentials).toEqual([]);
  });
});
