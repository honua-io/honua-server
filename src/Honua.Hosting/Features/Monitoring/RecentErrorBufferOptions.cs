// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Configuration options for the recent errors ring buffer.
/// </summary>
internal sealed class RecentErrorBufferOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Monitoring:RecentErrors";

    /// <summary>
    /// Maximum number of recent errors to retain.
    /// </summary>
    public int Capacity { get; set; } = 50;
}
