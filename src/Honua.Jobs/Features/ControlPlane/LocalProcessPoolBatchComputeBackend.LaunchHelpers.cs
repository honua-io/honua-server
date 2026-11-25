// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class LocalProcessPoolBatchComputeBackend
{
    private static readonly HashSet<string> BlockedWorkloadEnvironmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "DOTNET_ROOT",
        "DOTNET_HOST_PATH",
        "PYTHONPATH",
        "PYTHONSTARTUP",
        "PYTHONHOME",
        "NODE_OPTIONS",
        "NODE_PATH",
        "RUBYOPT",
        "RUBYLIB",
        "PERL5OPT",
        "PERL5LIB",
        "JAVA_TOOL_OPTIONS",
        "_JAVA_OPTIONS",
        "CLASSPATH",
        "BASH_ENV",
        "ENV",
        "CDPATH",
        "IFS",
        "SHELLOPTS",
        "PS4",
        "PATH",
        "PATHEXT",
        "COMSPEC",
        "SYSTEMROOT"
    };

    private static readonly string[] BlockedWorkloadEnvironmentPrefixes =
    [
        "LD_",
        "DYLD_",
        "CORECLR_",
        "COMPLUS_"
    ];

    private void ValidateLaunchParameters(ExecutionJobRecord job)
    {
        _ = ResolveWorkingDirectoryPath(job);
        ValidateWorkloadEnvironmentParameters(job.Spec.Parameters);
    }

    private (string Directory, bool OwnedScratch) ResolveWorkingDirectory(ExecutionJobRecord job)
    {
        var (directory, ownedScratch) = ResolveWorkingDirectoryPath(job);
        Directory.CreateDirectory(directory);
        return (directory, ownedScratch);
    }

    private (string Directory, bool OwnedScratch) ResolveWorkingDirectoryPath(ExecutionJobRecord job)
    {
        var root = string.IsNullOrWhiteSpace(_options.WorkingRoot)
            ? Path.Join(Path.GetTempPath(), "honua-local-process")
            : _options.WorkingRoot;
        var fullRoot = Path.GetFullPath(root);
        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) || fullRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (job.Spec.Parameters.TryGetValue(LocalProcessParameterKeys.WorkingDirectory, out var configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return (ResolveConfiguredWorkingDirectory(configured, fullRoot, rootWithSeparator), false);
        }

        var jobDirectory = Path.Join(fullRoot, SanitizeDirectorySegment(job.OperationId));
        return (jobDirectory, true);
    }

    private static string ResolveConfiguredWorkingDirectory(
        string configured,
        string fullRoot,
        string rootWithSeparator)
    {
        var trimmed = configured.Trim();
        if (trimmed.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Parameter '{LocalProcessParameterKeys.WorkingDirectory}' contains an invalid path.");
        }

        var candidate = Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Join(fullRoot, trimmed));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var withinRoot = string.Equals(candidate, fullRoot, comparison)
            || candidate.StartsWith(rootWithSeparator, comparison);
        if (!withinRoot)
        {
            throw new InvalidOperationException(
                $"Parameter '{LocalProcessParameterKeys.WorkingDirectory}' must resolve within the configured working root.");
        }

        return candidate;
    }

    private static string ResolveExecutable(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(LocalProcessParameterKeys.Executable, out var executable)
            || string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException(
                $"Local process execution requires parameter '{LocalProcessParameterKeys.Executable}'.");
        }

        return executable.Trim();
    }

    private string? ValidateExecutableContract(ExecutionJobRecord job, string executable)
    {
        var arguments = ResolveArguments(job.Spec.Parameters);
        var matchingVersions = _options.WorkerContracts
            .Where(contract => string.Equals(contract.Executable, executable, StringComparison.Ordinal))
            .Where(contract => ContractMatchesSelectedWorker(contract, arguments))
            .Select(contract => Math.Max(1, contract.MaxSupportedContractVersion))
            .ToArray();
        if (matchingVersions.Length == 0)
        {
            return $"Local process worker '{executable}' with the selected argument prefix is not registered in "
                + $"{LocalProcessPoolOptions.SectionName}:WorkerContracts; it cannot run a job that requires version {job.Spec.ContractVersion}.";
        }

        var supportedVersion = matchingVersions.Max();
        return job.Spec.ContractVersion <= supportedVersion
            ? null
            : $"Local process worker '{executable}' with the selected argument prefix supports execution contract "
                + $"version {supportedVersion}, "
                + $"but the job requires version {job.Spec.ContractVersion}.";
    }

    private static bool ContractMatchesSelectedWorker(
        LocalProcessWorkerContractOptions contract,
        string[] arguments)
    {
        var prefix = contract.ArgumentPrefix;
        if (prefix.Count == 0)
        {
            // Empty prefixes are accepted only under an explicit operator assertion.
            return contract.IsSelfContained;
        }

        return arguments.Length >= prefix.Count
            && prefix.Select((argument, index) => string.Equals(
                    argument,
                    arguments[index],
                    StringComparison.Ordinal))
                .All(static matches => matches);
    }

    private static string[] ResolveArguments(IReadOnlyDictionary<string, string> parameters)
    {
        var ordered = new SortedDictionary<int, string>();
        foreach (var entry in parameters)
        {
            if (!entry.Key.StartsWith(LocalProcessParameterKeys.ArgumentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var indexText = entry.Key[LocalProcessParameterKeys.ArgumentPrefix.Length..];
            if (int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                ordered[index] = entry.Value ?? string.Empty;
            }
        }

        return ordered.Count == 0 ? Array.Empty<string>() : ordered.Values.ToArray();
    }

    private static void ValidateWorkloadEnvironmentParameters(IReadOnlyDictionary<string, string> parameters)
    {
        foreach (var entry in parameters)
        {
            if (!entry.Key.StartsWith(LocalProcessParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.Key[LocalProcessParameterKeys.EnvironmentPrefix.Length..];
            if (string.IsNullOrWhiteSpace(name) || ExecutionWorkerLaunchEnvironment.IsReserved(name))
            {
                continue;
            }

            if (IsBlockedWorkloadEnvironmentName(name))
            {
                throw new InvalidOperationException(
                    $"Workload environment variable '{name}' is blocked for local process execution.");
            }
        }
    }

    private static bool IsBlockedWorkloadEnvironmentName(string name)
    {
        if (BlockedWorkloadEnvironmentNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in BlockedWorkloadEnvironmentPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<KeyValuePair<string, string>> BuildEnvironment(ExecutionJobRecord job, int executionAttempt)
    {
        // Same launch contract as the cloud backends (ExecutionWorkerLaunchEnvironment) so a workload
        // behaves identically whether it runs as a child process here or as a batch container.
        var variables = new List<KeyValuePair<string, string>>();
        foreach (var entry in job.Spec.Parameters)
        {
            if (!entry.Key.StartsWith(LocalProcessParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = entry.Key[LocalProcessParameterKeys.EnvironmentPrefix.Length..];
            if (string.IsNullOrWhiteSpace(name) || ExecutionWorkerLaunchEnvironment.IsReserved(name))
            {
                // Never let a workload passthrough shadow a launch variable; they are stamped last.
                continue;
            }

            if (IsBlockedWorkloadEnvironmentName(name))
            {
                throw new InvalidOperationException(
                    $"Workload environment variable '{name}' is blocked for local process execution.");
            }

            variables.Add(new(name, entry.Value ?? string.Empty));
        }

        variables.AddRange(ExecutionWorkerLaunchEnvironment.Build(job, executionAttempt));
        return variables;
    }

    private static string SanitizeDirectorySegment(string operationId)
    {
        var chars = operationId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
            {
                chars[i] = '-';
            }
        }

        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "job" : sanitized;
    }
}
