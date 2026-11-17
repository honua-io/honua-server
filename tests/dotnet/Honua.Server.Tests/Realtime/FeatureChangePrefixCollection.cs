// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Realtime;

// PostgreSQL's snapshot xmin includes transactions in other databases on the cluster.
// Prevent unrelated integration fixtures from holding the publisher's retirement fence.
[CollectionDefinition("FeatureChangePrefix", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition")]
public sealed class FeatureChangePrefixCollection;
