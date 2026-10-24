// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

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
