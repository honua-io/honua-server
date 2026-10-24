// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Serializes tests that mutate HonuaTelemetry's process-wide exception recording settings.
/// </summary>
[CollectionDefinition("HonuaTelemetry", DisableParallelization = true)]
public sealed class HonuaTelemetryCollectionDefinition
{
}
