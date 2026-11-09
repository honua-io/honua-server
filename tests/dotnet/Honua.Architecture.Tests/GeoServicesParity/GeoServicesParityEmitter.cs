// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Xunit;

namespace Honua.Architecture.Tests.GeoServicesParity;

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
