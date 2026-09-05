// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

public sealed class StudioPackageValidatorCompositionTests
{
    [Theory]
    [InlineData("{\"layers\":\"bad\"}")]
    [InlineData("{\"layers\":null}")]
    [InlineData("{\"layers\":[null]}")]
    [InlineData("{\"layers\":[{}]}")]
    [InlineData("{\"layers\":[{\"id\":\"same\"},{\"id\":\"same\"}]}")]
    [InlineData("{\"widgets\":{}}")]
    [InlineData("{\"widgets\":[null]}")]
    [InlineData("{\"widgets\":[{\"id\":\"legend\"}]}")]
    [InlineData("{\"view\":\"bad\"}")]
    [InlineData("{\"view\":{\"center\":[1]}}")]
    [InlineData("{\"view\":{\"bbox\":[4,3,2,1]}}")]
    [InlineData("{\"view\":{\"zoom\":25}}")]
    [InlineData("{\"view\":{\"pitch\":86}}")]
    public void Validate_DashboardMalformedCompositionWithoutStandardBlocks_IsInvalid(string body)
    {
        var result = Validate(body);
        Assert.Equal(StudioPackageValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code.StartsWith("studio.composition.", StringComparison.Ordinal));
    }

    [UnitTest]
    public void Validate_DashboardCompositionAtViewportBounds_IsValid()
    {
        var result = Validate("""
            {"layers":[{"id":"roads","visible":false}],"widgets":[{"id":"legend","kind":"legend"}],
             "view":{"center":[-158,22],"bbox":[-159,21,-157,23],"zoom":24,"pitch":85}}
            """);
        Assert.Equal(StudioPackageValidationStatus.Valid, result.Status);
        Assert.Empty(result.Diagnostics);
    }

    private static StudioValidationSummary Validate(string body)
    {
        using var document = JsonDocument.Parse(body);
        var validator = new StudioPackageValidator(
            new StudioPackageFamilyRegistry(new InMemoryStudioPackageStore()), TimeProvider.System);
        return validator.Validate(new StudioPackageEnvelope
        {
            Family = StudioPackageFamily.Dashboard,
            Format = "studio_dashboard_package.v1",
            SchemaVersion = "1.0",
            Body = document.RootElement.Clone(),
        });
    }
}
