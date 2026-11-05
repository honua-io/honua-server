// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Hosting;

// The entry point returns on a refusal so native finalizers do not run after their modules unload.
internal static class StartupRefusal
{
    internal const int ExitCode = GatedBootShutdown.RefusalExitCode;

    internal static void Exit(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        Environment.ExitCode = ExitCode;
        Console.Error.WriteLine(message);
    }
}
