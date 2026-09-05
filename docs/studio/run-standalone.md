# Run Studio standalone

{% hint style="danger" %}
**Blocked in 2026.1 preview.** There is no released, versioned Studio static
bundle or container to install. Do not deploy an image name or `/config.json`
shape copied from an unmerged branch.
{% endhint %}

The source repository has a development server, an embeddable custom element,
and OIDC Authorization Code with PKCE. Those are development evidence, not a
self-hosted product artifact. A supported standalone procedure requires a
versioned bundle and image, runtime server/OIDC configuration without a
rebuild, a clean-machine candidate receipt, and a real-model compose,
durable-save, and reopen receipt.

Track [honua-studio#41](https://github.com/honua-io/honua-studio/issues/41).
Until it closes with release evidence, use [embedding](embed.md) from a source
checkout for evaluation, or drive the server directly with MCP or an SDK.
Neither route promotes the browser Studio preview to GA support.
