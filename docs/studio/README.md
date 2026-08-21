# Honua Studio (preview)

Honua Studio is the JavaScript authoring surface for building maps and apps with
Honua. One canvas can be driven by direct UI gestures, an AI model, or an
external MCP host; every mutation still goes through the same typed Studio
tools and the server-owned draft lifecycle.

Studio is a **preview** in 2026.1. The supported release journey is self-hosted:
run the static Studio artifact, connect it to your Honua server and identity
provider, choose a server-side model provider, compose and reopen durable
drafts, then ask a human to approve publication in Console. There is no
Honua-hosted demo model in 2026.1.

Start with:

- [Run Studio standalone](run-standalone.md)
- [Embed Studio](embed.md)
- [Bring your own model](byom.md)
- [Studio MCP tools](mcp-tools.md)
- [Drive Studio from Claude Desktop](drive-from-claude-desktop.md)
- [Use StudioAgentSession](sdk-js-studio-agent.md)
- [Use the .NET lifecycle client](dotnet-lifecycle.md)

The approval fence is deliberate: an agent can prepare a publication intent,
but cannot publish, share, or embed content. Studio creates a request handle and
polls it; only a human Console action can create the public route.
