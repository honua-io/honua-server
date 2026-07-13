// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Regeneration entry point for the committed feature catalog
/// (<c>docs/gis/data/feature-catalog.json</c>). This is the canonical way to
/// re-emit the artifact after adding an endpoint or a proving test.
/// </summary>
/// <remarks>
/// The emitter is a normal <see cref="FactAttribute"/> gated behind the
/// <c>HONUA_EMIT_FEATURE_CATALOG</c> environment variable so it never writes
/// during ordinary <c>dotnet test</c> runs (which must stay read-only and pass
/// the drift guard). Regenerate with:
/// <code>
/// scripts/generate-feature-catalog.sh
/// # or directly:
/// HONUA_EMIT_FEATURE_CATALOG=1 dotnet test \
///   tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
///   --filter "FullyQualifiedName~FeatureCatalogEmitter"
/// </code>
/// </remarks>
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
