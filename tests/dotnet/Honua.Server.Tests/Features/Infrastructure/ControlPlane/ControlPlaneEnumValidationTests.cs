// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ControlPlaneEnumValidationTests
{
    private readonly ControlPlaneOptionsValidator _validator = new();

    public static TheoryData<string> DefinedDeployTargetKinds() => new(Enum.GetNames<DeployTargetKind>());

    [UnitTheory]
    [MemberData(nameof(DefinedDeployTargetKinds))]
    public void Validate_DeployTargetWithDefinedTargetKind_Succeeds(string targetKind)
    {
        var options = Bind(new() { ["ControlPlane:DeployTargets:0:TargetKind"] = targetKind });

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    [UnitTheory]
    [InlineData("-1")]
    [InlineData("2")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("99")]
    public void Validate_DeployTargetWithUndefinedNumericTargetKind_FailsStartup(string targetKind)
    {
        var options = Bind(new() { ["ControlPlane:DeployTargets:0:TargetKind"] = targetKind });

        var result = _validator.Validate(null, options);

        AssertSingleEnumFailure(result.Failures, "ControlPlane:DeployTargets:0:TargetKind", nameof(DeployTargetKind.LocalCompose));
    }

    private static void AssertSingleEnumFailure(IEnumerable<string>? failures, string property, string allowedName)
    {
        var failure = Assert.Single(failures ?? []);
        Assert.StartsWith($"{property} value ", failure, StringComparison.Ordinal);
        Assert.Contains(allowedName, failure, StringComparison.Ordinal);
    }

    private static ControlPlaneOptions Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(ControlPlaneOptions.SectionName)
            .Get<ControlPlaneOptions>()!;
}
