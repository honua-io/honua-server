# Honua Studio

{% hint style="warning" %}
**Preview in Honua 2026.1.** Browser Studio surfaces and their contracts may
change. Preview does not carry a promise of general-availability support or
compatibility. The server APIs they use have their own documented contracts.
{% endhint %}

Honua Studio is one JavaScript map-application composer. A person or model
works on the same typed composition: the canvas renders it, while typed tools
add layers and controls, change styles and view state, bind interactions, and
manage drafts. The model never edits the DOM or invents server routes.

Use the preview by embedding `<honua-studio-app>`, using Honua's model proxy
with the JavaScript `StudioAgentSession`, or connecting another MCP host to
`/mcp` and calling the same composition tools.

The server candidate verified for this section is
`d4da482c95db7cb4d0dc06958b232c64a52a7b36`. Verification covered names,
routes, configuration shapes, documentation links, and the repository's
documentation-only pre-PR gate. It did not turn an unreleased browser bundle
into a supported distribution.

## Release boundary

The embeddable element contract, AI proxy, MCP tools, and SDK clients exist.
A versioned standalone Studio bundle/container and its runtime `/config.json`
contract have not shipped: [honua-studio#41](https://github.com/honua-io/honua-studio/issues/41)
is blocking. The approval-backed publication journey is also blocked by
[honua-server#3304](https://github.com/honua-io/honua-server/issues/3304).
These pages document composition, durable save/reopen, and currently
executable client surfaces, but do not claim a completed browser publication
journey.
