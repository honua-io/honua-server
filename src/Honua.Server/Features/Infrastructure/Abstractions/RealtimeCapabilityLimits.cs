// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Abstractions;

internal sealed class RealtimeCapabilityLimits(IOptions<RealtimeOptions> options)
{
    public TimeSpan FeatureRetention => options.Value.Retention.GetValueOrDefault(RealtimeLogFamilies.Features, TimeSpan.FromHours(24));

    public int CursorRetentionLimit => options.Value.ResumeMaxEvents;
}
