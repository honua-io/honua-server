# Run Studio standalone

Honua Studio is a static single-page application. Its 2026.1 container serves
one immutable bundle and generates `/config.json` from environment variables at
startup, so changing the server, OIDC tenant, or model route does not require a
frontend rebuild.

From a checkout of `honua-studio`:

```bash
docker build -t honua-studio:2026.1 .
docker run --rm -p 8080:8080 \
  -e HONUA_SERVER_BASE_URL=https://honua.example.com \
  -e HONUA_OIDC_ISSUER=https://idp.example.com/realms/honua \
  -e HONUA_OIDC_CLIENT_ID=honua-studio \
  -e HONUA_OIDC_REDIRECT_URI=http://localhost:8080/ \
  -e HONUA_OIDC_SCOPES="openid profile email" \
  -e HONUA_MODEL_PROVIDER=local-ollama \
  -e HONUA_MODEL=qwen2.5:7b \
  honua-studio:2026.1
```

Open `http://localhost:8080`. `HONUA_SERVER_BASE_URL` is the server origin
before `/api/v1` and `/mcp`. Allow the Studio origin in the server's CORS and
OIDC client configuration. Use an authorization-code flow with PKCE; never put
a client secret or model credential in `/config.json`.

For a non-container static host, publish `dist/`, provide the same shape as
`public/config.json`, route SPA fallbacks to `index.html`, cache hashed assets,
and serve `config.json` with `Cache-Control: no-store`.

Production server replicas should share Redis. Studio's deterministic
`map_…`/`app_…` scratch drafts then retain their 24-hour TTL and capacity bounds
across replicas and restarts; without Redis the documented fallback is
single-process memory. The governed package lifecycle remains in its durable SQL
store.
