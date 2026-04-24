// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Serializes tests that listen to the shared execution-transition meter.
/// </summary>
[CollectionDefinition("ControlPlaneTransitionTelemetry", DisableParallelization = true)]
public sealed class ControlPlaneTransitionTelemetryDefinition
{
}
