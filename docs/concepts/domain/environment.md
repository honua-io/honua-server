---
type: concept
title: "Environment"
description: "One Honua server managed by a Console."
tags: [gis, domain-language]
---

# Environment

An Environment is one Honua server that a Console manages. Development, staging, and production are common names for different Environments. The Console profile identifies server endpoints and authentication configuration; selecting an Environment selects where subsequent actions run.

A [Connection](connection.md) registers a database or file with that server. An Environment can contain many Connections, [Folders](folder.md), [Services](service.md), and [Layers](layer.md). Connecting the Console to a server does not register a database, publish a Dataset, or grant new permissions.

Check the selected Environment, authenticated identity, and effective capabilities before a consequential change. Moving content between Environments is an explicit promotion or deployment action, not a Folder move.

The metadata model also has an `environment` key that partitions snapshots. Resolve it against the selected server's configuration; matching names alone do not prove two snapshots or Console profiles refer to the same server. A [deployment](deployment.md) describes the artifact and infrastructure running behind an Environment.
