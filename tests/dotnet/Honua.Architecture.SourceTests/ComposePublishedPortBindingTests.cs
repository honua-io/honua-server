// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

// Compose publishes a port on every interface unless the mapping names a host address, so each
// shipped compose file binds loopback by default and leaves wider exposure to the operator.
[Trait("Category", "Architecture")]
public sealed partial class ComposePublishedPortBindingTests
{
    private static readonly string[] LoopbackHostPrefixes = ["127.0.0.1:", "${HONUA_BIND_ADDRESS:-127.0.0.1}:"];

    // Dot directories hold repository plumbing, workflow definitions and nested worktrees.
    private static readonly HashSet<string> BuildOutputDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", "artifacts", "TestResults"
    };

    [ArchitectureTest]
    public void ComposeFiles_PublishPortsOnLoopbackByDefault()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var published = EnumerateComposeFiles(repositoryRoot)
            .SelectMany(path => ReadPublishedPorts(repositoryRoot, path))
            .ToList();

        published.Should().NotBeEmpty("the repository ships compose files that publish ports");
        published
            .Where(static port => !LoopbackHostPrefixes.Any(prefix => port.Mapping.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(static port => $"{port.ComposeFile}:{port.Line} {port.Mapping}")
            .Should().BeEmpty(
                "a published port must be a dash-list entry in the short form that starts with ${{HONUA_BIND_ADDRESS:-127.0.0.1}}: or 127.0.0.1:, so it is not reachable from other hosts by default");
    }

    [ArchitectureTest]
    public void ComposeFiles_KeepPrometheusLifecycleRoutesOff()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        EnumerateComposeFiles(repositoryRoot)
            .Where(static path => File.ReadAllText(path).Contains("--web.enable-lifecycle", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Should().BeEmpty("the Prometheus reload and quit routes take no credentials when enabled");
    }

    private static IEnumerable<string> EnumerateComposeFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)
                     .Where(static file => Path.GetExtension(file) is ".yml" or ".yaml")
                     .Where(static file => TopLevelServicesKey().IsMatch(File.ReadAllText(file))))
        {
            yield return file;
        }

        foreach (var composeFile in Directory.EnumerateDirectories(directory)
                     .Where(static child => !IsSkipped(Path.GetFileName(child)))
                     .SelectMany(EnumerateComposeFiles))
        {
            yield return composeFile;
        }
    }

    private static bool IsSkipped(string directoryName)
        => directoryName.StartsWith('.') || BuildOutputDirectories.Contains(directoryName);

    private static IEnumerable<PublishedPort> ReadPublishedPorts(string repositoryRoot, string path)
    {
        var relativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
        var lines = File.ReadAllLines(path);
        var insidePorts = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (PortsKey().Match(line) is { Success: true } portsKey)
            {
                var inlineValue = portsKey.Groups["inline"].Value;
                foreach (var mapping in ReadInlineMappings(inlineValue))
                {
                    yield return new PublishedPort(relativePath, index + 1, mapping);
                }

                insidePorts = inlineValue.Length == 0;
                continue;
            }

            if (!insidePorts || string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var entry = PortEntry().Match(line);
            if (!entry.Success)
            {
                insidePorts = false;
                continue;
            }

            yield return new PublishedPort(relativePath, index + 1, entry.Groups["mapping"].Value);
        }
    }

    // An inline list is read entry by entry. An anchor or alias cannot be followed line by line,
    // so it is reported as written and fails the loopback check.
    private static IEnumerable<string> ReadInlineMappings(string inlineValue)
    {
        if (inlineValue.Length == 0)
        {
            return [];
        }

        return inlineValue.StartsWith('[') && inlineValue.EndsWith(']')
            ? inlineValue[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static entry => entry.Trim('"', '\''))
            : [inlineValue];
    }

    [GeneratedRegex(@"^services:\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TopLevelServicesKey();

    [GeneratedRegex(@"^\s*ports:\s*(?<inline>[^#\s][^#]*?)?\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PortsKey();

    [GeneratedRegex(@"^\s*-\s*[""']?(?<mapping>[^""'#]+?)[""']?\s*(?:#.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PortEntry();

    private sealed record PublishedPort(string ComposeFile, int Line, string Mapping);
}
