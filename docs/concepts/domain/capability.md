---
type: concept
title: "Capability and availability"
description: "A declared operation or surface whose effective availability depends on the deployed configuration and authority."
tags: [gis, domain-language]
aliases: ["supported","enabled","available","edition","license","tool discovery"]
sources:
  - id: contract
    resource: "/guides/deploy/capability-deployment-profiles.md"
relations:
  - predicate: relates_to
    target: "/okf/capabilities/README.md"
  - predicate: relates_to
    target: "/concepts/domain/permissions.md"
  - predicate: relates_to
    target: "/guides/workflows/analyze-features.md"
  - predicate: relates_to
    target: "/guides/workflows/install-configure.md"
---

# Capability and availability
A capability key names a server contract. The existing [capability concepts](../../okf/capabilities/README.md) join documentation to the registry through `honua://capability/<key>`.

A deployment profile selects permitted capability keys; runtime licensing remains a separate check. Effective availability also depends on backend configuration and the caller's [permissions](permissions.md). A documentation page or advertised tool alone does not prove successful execution.[^contract]

“Supported on Lambda” needs the specific operation and configured runner, not just the hosting label. “The tool is listed” does not prove its persistent service is composed.

Before [analysis](../../guides/workflows/analyze-features.md) or [installation](../../guides/workflows/install-configure.md), inspect the actual server's discovery and structured limitations. Preserve preview or experimental status; this knowledge graph does not promote a feature to general availability.

[^contract]: [Source contract](../../guides/deploy/capability-deployment-profiles.md).
