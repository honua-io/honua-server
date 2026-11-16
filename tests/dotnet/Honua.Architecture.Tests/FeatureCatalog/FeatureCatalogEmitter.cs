// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

[Trait("Category", "Architecture")]
public sealed class FeatureCatalogEmitter
{
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

internal static class FeatureCatalogPaths
{
    public const string RelativePath = "docs/gis/data/feature-catalog.json";

    public static string CommittedArtifactPath()
        => Honua.Architecture.Tests.ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(),
            "docs",
            "gis",
            "data",
            "feature-catalog.json");
}
