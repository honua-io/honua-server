// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

[Trait("Category", "Architecture")]
public sealed class FeatureCatalogEmitter
{
    /// <summary>Environment variable that opts a run in to regenerating the artifact.</summary>
    public const string EmitEnvironmentVariable = "HONUA_EMIT_FEATURE_CATALOG";

    [Fact]
    public void Emit_FeatureCatalogJson_FromLiveSurface()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EmitEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            // Skip silently in normal test runs. Setting the env var makes the
            // emit explicit so the artifact is never rewritten by accident.
            return;
        }

        var catalog = FeatureCatalogGenerator.Generate();
        var json = FeatureCatalogGenerator.Serialize(catalog);
        File.WriteAllText(FeatureCatalogPaths.CommittedArtifactPath(), json);
    }
}

/// <summary>Resolves the committed feature-catalog artifact path.</summary>
internal static class FeatureCatalogPaths
{
    /// <summary>Repo-relative location of the committed catalog artifact.</summary>
    public const string RelativePath = "docs/gis/data/feature-catalog.json";

    /// <summary>Absolute path to <c>docs/gis/data/feature-catalog.json</c>.</summary>
    public static string CommittedArtifactPath()
        => Honua.Architecture.Tests.ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            "docs",
            "gis",
            "data",
            "feature-catalog.json");
}
