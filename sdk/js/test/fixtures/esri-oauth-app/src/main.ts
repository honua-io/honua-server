import esriConfig from "@arcgis/core/config";
import OAuthInfo from "@arcgis/core/identity/OAuthInfo";
import IdentityManager from "@arcgis/core/identity/IdentityManager";

const info = new OAuthInfo({
  appId: "client-id",
  portalUrl: "https://portal.example.test",
  popup: true,
});

IdentityManager.registerOAuthInfos([info]);
esriConfig.portalUrl = "https://portal.example.test";
void IdentityManager.checkSignInStatus(`${esriConfig.portalUrl}/sharing`);
