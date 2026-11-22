---
type: guide
title: "Embed Studio"
description: "Embedding is a preview browser surface in 2026.1."
---
# Embed Studio

Embedding is a **preview** browser surface in 2026.1. The Studio element kit
defines `<honua-studio-app>` and the other `honua-studio-*` custom elements. It
is not published to a package registry: build it from the Studio source or a
versioned release archive. Importing the kit defines nothing; the host registers
the elements it uses. A host owns authentication
and assigns a short-lived session adapter; Studio must not receive model,
provider, or administrator credentials.

```ts
import { registerStudioElement } from "@honua/studio/elements";

registerStudioElement("honua-studio-app");

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
