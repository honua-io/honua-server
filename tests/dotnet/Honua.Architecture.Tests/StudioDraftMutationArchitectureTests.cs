// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>Decrement-only ratchet for the Slice 3a Studio mutation conversion.</summary>
public sealed class StudioDraftMutationArchitectureTests
{
    private static readonly string[] KnownRemainingDirectMutationSites =
    [
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|ValidateDraftAsync",
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|PreviewPlanAsync",
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|SaveDraftAsVersionAsync",
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|CreatePublicationRequestAsync",
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|ReopenVersionAsync",
        "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs|RollbackAsync",
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioDraftToolBase.cs|UpdateDraftAsync",
    ];

    [ArchitectureTest]
    public void ConvertedStudioMutationSites_CannotCallLifecycleActuatorsDirectly()
    {
        var root = FindRepositoryRoot();
        var endpoints = File.ReadAllText(Path.Join(
            root, "src", "Honua.Server", "Features", "Studio", "StudioPackageEndpoints.cs"));
        var lifecycleTools = File.ReadAllText(Path.Join(
            root, "src", "Honua.Ai", "Features", "Protocols", "Mcp", "Mcp", "Studio", "StudioDraftLifecycleTools.cs"));

        Assert.DoesNotContain("service.CreateDraftAsync(", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("service.UpdateDraftAsync(", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("service.DeleteDraftAsync(", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycleService.CreateDraftAsync(", lifecycleTools, StringComparison.Ordinal);
        Assert.Contains("mutationRuntime.CreateAsync(", endpoints, StringComparison.Ordinal);
        Assert.Contains("mutationRuntime.UpdateAsync(", endpoints, StringComparison.Ordinal);
        Assert.Contains("mutationRuntime.DeleteAsync(", endpoints, StringComparison.Ordinal);
        Assert.Contains("mutationRuntime.CreateAsync(", lifecycleTools, StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void RemainingStudioLifecycleMutationBypasses_MatchPinnedDecrementOnlyList()
    {
        var root = FindRepositoryRoot();
        var candidates = new[]
        {
            "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs",
            "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioDraftLifecycleTools.cs",
            "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioDraftToolBase.cs",
            "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioCompositionTools.cs",
            "src/Honua.Ai/Features/Protocols/Mcp/Mcp/Studio/StudioProposePublicationTool.cs",
        };
        var mutationMethods = new[]
        {
            "CreateDraftAsync", "UpdateDraftAsync", "DeleteDraftAsync", "ValidateDraftAsync",
            "PreviewPlanAsync", "SaveDraftAsVersionAsync", "SaveDraftAsCheckpointVersionAsync",
            "CreatePublicationRequestAsync", "ReopenVersionAsync", "RollbackAsync",
        };

        var actual = candidates
            .SelectMany(path => File.ReadLines(Path.Join(root, path.Replace('/', Path.DirectorySeparatorChar)))
                .Where(line => line.Contains("await ", StringComparison.Ordinal))
                .SelectMany(line => mutationMethods
                    .Where(method => line.Contains($".{method}(", StringComparison.Ordinal))
                    .Select(method => $"{path}|{method}")))
            .OrderBy(static site => site, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            KnownRemainingDirectMutationSites.OrderBy(static site => site, StringComparer.Ordinal),
            actual);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
