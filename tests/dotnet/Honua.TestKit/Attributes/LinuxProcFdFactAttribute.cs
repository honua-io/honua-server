// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test that requires Linux <c>/proc/self/fd</c> descriptor inspection.
/// </summary>
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
