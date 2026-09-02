// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Prevents a Studio MCP tool from loading a lifecycle draft outside the
/// centralized load-then-owner-authorize boundary (#3412).
/// </summary>
[Trait("Category", "Architecture")]
public sealed class StudioMcpOwnershipAuthorizationGuardTests
{
    private const string StudioToolsRelativePath =
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio";
    private const string RegistrationRelativePath =
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpServiceCollectionExtensions.cs";

    private static readonly Regex StudioToolRegistration = new(
        @"ServiceDescriptor\.Singleton<IMcpTool,\s*(?<type>\w*Studio\w*Tool)>\(\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void EveryRegisteredStudioLifecycleTool_FunnelsThroughOwnerAuthorization()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var studioRoot = ArchitectureTestHelpers.CombinePath(repoRoot, StudioToolsRelativePath);
        var registrationPath = ArchitectureTestHelpers.CombinePath(repoRoot, RegistrationRelativePath);
        var registrationSource = File.ReadAllText(registrationPath);
        var registeredTypes = StudioToolRegistration.Matches(registrationSource)
            .Select(match => match.Groups["type"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        registeredTypes.Should().HaveCount(17,
            "the complete honua_studio lifecycle/composition/proposal roster must remain under this guard");

        var sourceFiles = Directory.EnumerateFiles(studioRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllText, StringComparer.Ordinal);
        var baseSource = sourceFiles["StudioDraftToolBase.cs"];
        baseSource.Should().Contain("IStudioAuthorizationService");
        baseSource.Should().Contain("RequireAuthorizedDraftAsync");
        baseSource.Should().Contain("authorization.AuthorizeAsync(");

        var bypasses = sourceFiles
            .Where(pair => pair.Key != "StudioDraftToolBase.cs" && pair.Value.Contains(".GetDraftAsync(", StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        bypasses.Should().BeEmpty(
            "raw Studio draft loads outside StudioDraftToolBase bypass the canonical loaded-owner check");

        var compositionSource = sourceFiles["StudioCompositionTools.cs"];
        var compositionBaseSource = sourceFiles["StudioCompositionToolBase.cs"];
        compositionBaseSource.Should().Contain("RequireAuthorizedDraftAsync(",
            "every composition tool delegates its load/mutation to this single owner-authorized helper");

        foreach (var type in registeredTypes)
        {
            var declaration = FindDeclaration(sourceFiles, type);
            if (declaration is not { } resolvedDeclaration)
            {
                throw new InvalidOperationException(
                    $"Registered Studio tool '{type}' must have an auditable source declaration.");
            }

            if (type is "CreateStudioDraftTool" or "ProposeStudioPublicationTool")
            {
                ExtractClassBody(resolvedDeclaration.Source, resolvedDeclaration.Index)
                    .Should().Contain("EnsureStudioAuthorizedAsync(",
                        "item-scoped operations must authorize a resolved caller/existing item owner before persistence");
                continue;
            }

            if (compositionSource.Contains($"class {type} : StudioCompositionToolBase", StringComparison.Ordinal))
            {
                continue;
            }

            ExtractClassBody(resolvedDeclaration.Source, resolvedDeclaration.Index)
                .Should().Contain("RequireAuthorizedDraftAsync(",
                    $"'{type}' must load and authorize the recorded draft owner before lifecycle execution");
        }
    }

    private static (string Source, int Index)? FindDeclaration(
        IReadOnlyDictionary<string, string> sourceFiles,
        string type)
    {
        var marker = $"class {type}";
        foreach (var source in sourceFiles.Values)
        {
            var index = source.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                return (source, index);
            }
        }

        return null;
    }

    private static string ExtractClassBody(string source, int classIndex)
    {
        var nextClass = source.IndexOf("internal sealed class ", classIndex + 1, StringComparison.Ordinal);
        return nextClass < 0 ? source[classIndex..] : source[classIndex..nextClass];
    }
}
