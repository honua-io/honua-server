// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Unit tests for durable feature-stream replay continuity.
/// </summary>
public sealed class FeatureStreamReplayTests
{
    [UnitTest]
    public void HasReplayWindowGap_ContiguousAndSkippedCursors_DistinguishesWindowLoss()
    {
        Assert.False(FeatureStreamEndpoints.HasReplayWindowGap(41, 42));
        Assert.True(FeatureStreamEndpoints.HasReplayWindowGap(41, 43));
    }
}
