// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

// Realtime features depend on IRealtimeFanoutProvider only, so another provider can be added
// without touching them (ADR-0085, specification section 4).
public sealed partial class RealtimeProviderIsolationTests
{
    private static readonly string[] _allowedFiles =
    [
        "src/Honua.Server/Features/Realtime/RealtimeServiceCollectionExtensions.cs",
        "src/Honua.Server/Features/Realtime/RealtimeLogFanoutProvider.cs"
    ];

    [ArchitectureTest]
    public void ConcreteRealtimeProviders_AreNamedOnlyByTheirRegistration()
    {
        var root = FindRepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Join(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal))
            .Where(path => !_allowedFiles.Contains(path, StringComparer.Ordinal))
            .Where(path => ConcreteProvider().IsMatch(File.ReadAllText(Path.Join(root, path))))
            .ToArray();

        offenders.Should().BeEmpty(
            "only Features/Realtime/RealtimeServiceCollectionExtensions.cs chooses a realtime provider; depend on IRealtimeFanoutProvider instead");
    }

    [GeneratedRegex(@"\b(InProcessRealtimeFanoutProvider|PostgresRealtimeFanoutProvider|RealtimeLogFanoutProvider)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ConcreteProvider();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
