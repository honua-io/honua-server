// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class EditorConfigPolicyTests
{
    private static readonly string[] GlobalRulePrefixes =
    [
        "dotnet_diagnostic.",
        "dotnet_style_",
        "dotnet_naming_",
        "dotnet_sort_",
        "dotnet_separate_",
        "csharp_style_",
        "csharp_prefer_",
        "csharp_preferred_",
        "csharp_using_",
        "csharp_new_line_",
        "csharp_indent_",
        "csharp_space_",
        "csharp_preserve_",
    ];

    private static readonly string[] ExpectedGlobalCSharpRules =
    [
        "dotnet_diagnostic.CA1051.severity",
        "dotnet_diagnostic.CA1848.severity",
        "dotnet_diagnostic.CA2016.severity",
        "dotnet_diagnostic.IDE0044.severity",
        "dotnet_diagnostic.CA1825.severity",
        "dotnet_diagnostic.CA1859.severity",
        "dotnet_style_require_accessibility_modifiers",
        "csharp_using_directive_placement",
        "csharp_prefer_static_local_function",
    ];

    [ArchitectureTest]
    public void EditorConfig_GlobalCSharpRules_ApplyUnderGlobalSection()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var editorConfigPath = ArchitectureTestHelpers.CombinePath(repositoryRoot, ".editorconfig");
        File.Exists(editorConfigPath).Should().BeTrue();

        var (globalKeys, misplaced) = InspectGlobalCSharpRules(editorConfigPath);

        misplaced.Should().BeEmpty(
            "global C#/.NET coding conventions and analyzer severities must sit under [*.cs], not under a path-specific override");
        globalKeys.Should().Contain(ExpectedGlobalCSharpRules);
    }

    private static (HashSet<string> GlobalKeys, List<string> Misplaced) InspectGlobalCSharpRules(string editorConfigPath)
    {
        var misplaced = new List<string>();
        var globalKeys = new HashSet<string>(StringComparer.Ordinal);
        string? currentSection = null;
        var lineNumber = 0;

        foreach (var rawLine in File.ReadLines(editorConfigPath))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line;
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (string.Equals(currentSection, "[*.cs]", StringComparison.Ordinal))
            {
                globalKeys.Add(key);
            }
            else if (FormatMisplacedRule(key, currentSection, lineNumber) is { } violation)
            {
                misplaced.Add(violation);
            }
        }

        return (globalKeys, misplaced);
    }

    private static string? FormatMisplacedRule(string key, string? currentSection, int lineNumber)
    {
        return GlobalRulePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))
            ? $"line {lineNumber}: '{key}' sits under '{currentSection ?? "<root>"}' instead of '[*.cs]'"
            : null;
    }
}
