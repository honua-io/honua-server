# Embed Studio

Embedding is a **preview** browser surface in 2026.1. The source package
registers one `<honua-studio-app>` custom element. A host owns authentication
and assigns a short-lived session adapter; Studio must not receive model,
provider, or administrator credentials.

```ts
import "@honua/studio";

const studio = document.querySelector("honua-studio-app");
studio.session = {
  getToken: () => hostSession.getAccessToken(),
  onExpired: (listener) => hostSession.onExpired(listener),
};
```

Assigning `.session` before or after the element connects is supported by the
source contract. The host remains responsible for token renewal and expiry.
The window-global handoff is an alternative for hosts that cannot set the
property during bootstrap; the property is the primary interface.

See the canonical [element contract](https://github.com/honua-io/honua-studio/blob/main/docs/element-contract.md)
and [session handoff contract](https://github.com/honua-io/honua-studio/blob/main/docs/embed-session.md).
There is no GA compatibility promise for these preview element properties.
