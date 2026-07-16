// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests.GeoServicesParity;

/// <summary>
/// Regeneration entry point for the published GeoServices REST parity matrix
/// (<c>docs/gis/data/geoservices-rest-parity.json</c>). This is the canonical way to
/// re-emit the artifact after editing the judgement source or after adding,
/// removing, or renaming a GeoServices route.
/// </summary>
/// <remarks>
/// Mirrors <c>FeatureCatalogEmitter</c>: a normal <see cref="FactAttribute"/> gated
/// behind an environment variable so it never writes during ordinary
/// <c>dotnet test</c> runs, which must stay read-only and pass the drift guard.
/// Regenerate with:
/// <code>
/// scripts/generate-geoservices-parity.sh
/// # or directly:
/// HONUA_EMIT_GEOSERVICES_PARITY=1 dotnet test \
///   tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
///   --filter "FullyQualifiedName~GeoServicesParityEmitter"
/// </code>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class GeoServicesParityEmitter
{
    /// <summary>Environment variable that opts a run in to regenerating the artifact.</summary>
    public const string EmitEnvironmentVariable = "HONUA_EMIT_GEOSERVICES_PARITY";

    [Fact]
    public void Emit_GeoServicesParityJson_FromDerivedRosterAndJudgment()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EmitEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            // Skip silently in normal runs. Setting the env var makes the emit
            // explicit so the published artifact is never rewritten by accident.
            return;
        }

        var matrix = GeoServicesParityGenerator.Generate();
        var json = GeoServicesParityGenerator.Serialize(matrix);
        File.WriteAllText(GeoServicesParityGenerator.CommittedMatrixPath(), json);
    }
}
