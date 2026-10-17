---
type: guide
title: "Run Studio standalone"
description: "Build and configure the self-hosted Studio Preview from source, or use a verified versioned release when available."
---
# Run Studio standalone

Studio has a self-hosted container build, static-archive release workflow, and
runtime `/config.json` contract. Build availability, published release assets,
and GA support are separate: Studio remains Preview.

1. Select the Studio source revision or a published release with its source/image
   receipt. The [Studio release posture](https://github.com/honua-io/honua-studio/blob/main/docs/preview-release-posture.md)
   defines the required gates. If no versioned release exists, build from source;
   do not assume a sample registry tag is pullable.
2. Follow the [self-hosted guide](https://github.com/honua-io/honua-studio/blob/main/docs/self-hosted.md)
   for that revision. It defines the container invocation and runtime settings.
3. Configure the server and MCP bases, public OIDC client, redirect URI, and model
   mode through that version's runtime configuration contract. Provider keys and
   administrator credentials do not belong in the browser configuration.
4. Verify sign-in, a realtime composition change, save, and reopen against the
   intended server. A container starting does not establish those outcomes.

The local BYOM/browser fixture receipt and hosted-model qualification have
different scopes. Use the linked release posture to interpret each; neither a
source build nor a working preview establishes general-availability support.

For an application host, use [embedding](embed.md). To work through an existing
agent client, use [MCP](drive-from-claude-desktop.md) or the
[JavaScript Studio agent](sdk-js-studio-agent.md).
