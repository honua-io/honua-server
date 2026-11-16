// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

public sealed class LinuxProcFdFactAttribute : FactAttribute
{
    public LinuxProcFdFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc/self/fd"))
        {
            Skip = "Requires Linux /proc/self/fd descriptor inspection.";
        }
    }
}
