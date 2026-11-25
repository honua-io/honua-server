// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;

namespace Honua.ControlPlane;

internal static class LocalProcessParameterKeys
{
    public const string Executable = "process.executable";

    // Positional arguments are passed as a list, never a shell string, so there is no shell interpretation.
    public const string ArgumentPrefix = "process.arg.";

    public const string WorkingDirectory = "process.working_dir";

    public const string EnvironmentPrefix = "env.";
}

internal sealed class LocalProcessPoolOptions
{
    public const string SectionName = "ControlPlane:LocalProcess";

    public int MaxConcurrentProcesses { get; set; } = 2;

    public string? WorkingRoot { get; set; }

    public List<LocalProcessWorkerContractOptions> WorkerContracts { get; set; } = [];
}

internal sealed class LocalProcessWorkerContractOptions
{
    public string Executable { get; set; } = string.Empty;

    public List<string> ArgumentPrefix { get; set; } = [];

    public bool IsSelfContained { get; set; }

    public int MaxSupportedContractVersion { get; set; } = 1;
}

internal sealed class LocalProcessPoolOptionsValidator : OptionsValidator<LocalProcessPoolOptions>
{
    protected override void ValidateOptions(LocalProcessPoolOptions options, List<string> failures)
    {
        if (options.MaxConcurrentProcesses <= 0)
        {
            failures.Add($"{LocalProcessPoolOptions.SectionName}:MaxConcurrentProcesses must be greater than 0.");
        }
    }
}
