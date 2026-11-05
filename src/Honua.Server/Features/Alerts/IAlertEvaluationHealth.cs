// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Alerts;

// The signal distinguishes a healthy follower from a hung leader and from every acquisition attempt faulting.
internal interface IAlertEvaluationHealth
{
    bool IsEvaluatorEnabled { get; }

    bool IsEvaluatorRunning { get; }

    bool IsLeader { get; }

    DateTimeOffset? LastHeartbeatAt { get; }

    DateTimeOffset? LastLeaderPassAt { get; }

    DateTimeOffset? LeaderAcquisitionFailingSince { get; }
}
