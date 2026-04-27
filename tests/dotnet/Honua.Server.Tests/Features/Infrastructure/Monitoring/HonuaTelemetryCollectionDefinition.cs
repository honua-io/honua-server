// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Serializes tests that mutate HonuaTelemetry's process-wide exception recording settings.
/// </summary>
[CollectionDefinition("HonuaTelemetry", DisableParallelization = true)]
public sealed class HonuaTelemetryCollectionDefinition
{
}
