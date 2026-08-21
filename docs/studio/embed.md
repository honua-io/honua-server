# Embed Studio

Use the `honua-studio` custom element when Studio should live inside another
web application. The host owns authentication and session handoff; the element
owns the authoring canvas and emits typed lifecycle events. Do not pass model
credentials into element properties or browser storage.

```html
<honua-studio id="studio"></honua-studio>
<script type="module" src="/studio/honua-studio.js"></script>
<script type="module">
  const studio = document.querySelector("#studio");
  studio.session = {
    serverBaseUrl: "https://honua.example.com",
    accessToken: await hostTokenProvider.getAccessToken(),
  };
</script>
```

Treat the example as the integration shape, not a substitute for the versioned
element contract. The canonical contracts live in the Studio repository:

- [custom-element contract](https://github.com/honua-io/honua-studio/blob/main/docs/element-contract.md)
- [session handoff](https://github.com/honua-io/honua-studio/blob/main/docs/embed-session.md)
- [AI chat wire contract](https://github.com/honua-io/honua-studio/blob/main/docs/ai-chat-wire-contract.md)

The embedded and standalone applications use the same `/api/v1/studio/*` and
`/mcp` surfaces. Embedding does not weaken ownership, operator-grant, approval,
or rate-limit checks.
