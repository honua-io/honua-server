// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class ExternalPostgisWhereValidationTests
{
    [UnitTheory]
    [InlineData("active = true; DROP TABLE features")]
    [InlineData("active = true -- comment")]
    [InlineData("active = true /* comment */")]
    [InlineData("active = true OR delete FROM features")]
    public void Validate_UnsafePostgisWhere_IsRejected(string where)
    {
        var (violations, _) = ProcessPlanValidator.Validate(Plan(where), new BuiltInProcessCatalog());

        violations.Should().Contain(violation =>
            violation.Code == "INVALID_PARAMETER_VALUE"
            && violation.FieldPath == "steps[src].inputs.where");
    }

    [UnitTheory]
    [InlineData("active = true")]
    [InlineData("status = 'deleted'")]
    [InlineData("name = 'a;b'")]
    public void Validate_LegitimatePostgisWhere_IsAccepted(string where)
    {
        var (violations, _) = ProcessPlanValidator.Validate(Plan(where), new BuiltInProcessCatalog());

        violations.Should().NotContain(violation => violation.FieldPath == "steps[src].inputs.where");
        violations.Should().BeEmpty();
    }

    private static AnalysisPlan Plan(string where) => new()
    {
        PlanId = "plan-postgis",
        IntentId = "intent-postgis",
        Steps =
        [
            new AnalysisPlanStep
            {
                StepId = "src",
                Kind = AnalysisPlanStepKind.Geoprocess,
                ProcessId = "source.postgis",
                Inputs = new Dictionary<string, string>
                {
                    ["connectionName"] = "warehouse",
                    ["table"] = "features",
                    ["where"] = where
                }
            }
        ]
    };
}
