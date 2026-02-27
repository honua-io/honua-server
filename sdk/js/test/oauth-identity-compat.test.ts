import { afterEach, describe, expect, it } from "vitest";

import { identityManager, OAuthInfoCompat } from "../src/index.js";

afterEach(() => {
  identityManager.reset();
});

describe("OAuth/Identity compat", () => {
  it("stores OAuth info entries", () => {
    const info = new OAuthInfoCompat({
      appId: "app-123",
      portalUrl: "https://portal.example.test",
      popup: true,
      expiration: 90,
    });

    identityManager.registerOAuthInfos([info]);
    const stored = identityManager.oauthInfos;

    expect(stored).toHaveLength(1);
    expect(stored[0]?.appId).toBe("app-123");
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
