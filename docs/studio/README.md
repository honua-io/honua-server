---
type: index
title: "Honua Studio"
description: "One typed map, app, and dashboard composition that a person or an agent edits through the same tools, in browser preview for 2026.1."
---
# Honua Studio

{% hint style="warning" %}
**Preview in Honua 2026.1.** Browser Studio surfaces and their contracts may
change. Preview does not carry a promise of general-availability support or
compatibility. The server APIs they use have their own documented contracts.
{% endhint %}

Honua Studio is the realtime JavaScript map-application composer. A person or model
works on the same typed composition: the canvas renders it, while typed tools
add layers and controls, change styles and view state, bind interactions, and
manage drafts. Changes appear on the canvas as tools execute; the user refines
the result conversationally and can undo or redo composition changes. The model
never edits the DOM or invents server routes.

This replaces the former approval-first Console builders. Ordinary composition
is an interactive editing loop, not a proposal queue. Publication and operational
actions retain their separately documented policies; they do not define the
authoring experience. Console owns administration, Catalog, Operate, Share,
and its retained package/style editors.

Use the preview by embedding `<honua-studio-app>`, using Honua's model proxy
with the JavaScript `StudioAgentSession`, or connecting another MCP host to
`/mcp` and calling the same composition tools.

## Release boundary

The embeddable element, AI proxy, MCP tools, SDK-owned live agent loop, and
self-hosted packaging workflow exist. Studio's current source drives live turns
through `StudioAgentSession` and updates the canvas as tool calls stream.

Source implementation, published artifacts, and supported release scope are
separate. Check the [Studio README](https://github.com/honua-io/honua-studio/blob/main/README.md)
and [Preview release posture](https://github.com/honua-io/honua-studio/blob/main/docs/preview-release-posture.md)
for current packaging, model-quality, and browser evidence. The
[sharing posture](https://github.com/honua-io/honua-studio/blob/main/docs/share-preview-posture.md)
documents the separate publication journey. Realtime composition remains Preview.
