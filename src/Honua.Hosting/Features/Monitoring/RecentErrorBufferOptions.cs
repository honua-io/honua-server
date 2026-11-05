// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Monitoring;

internal sealed class RecentErrorBufferOptions
{
    public const string SectionName = "Monitoring:RecentErrors";

    public int Capacity { get; set; } = 50;
}
