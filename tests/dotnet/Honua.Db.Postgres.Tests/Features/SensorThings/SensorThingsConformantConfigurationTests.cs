// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.SensorThings;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Db.Postgres.Tests.Features.SensorThings;

/// <summary>
/// Verifies trim-safe conformant-profile configuration retains Boolean binding semantics.
/// </summary>
public sealed class SensorThingsConformantConfigurationTests
{
    [UnitTest]
    public void MissingConfiguration_DisablesConformantProfile()
    {
        Assert.False(PostgresObservationStore.IsConformantProfileEnabled(null));
    }

    [UnitTheory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("FALSE", false)]
    [InlineData(" True ", true)]
    [InlineData(" false ", false)]
    public void BooleanConfiguration_PreservesValue(string? value, bool expected)
    {
        var configuration = Configuration(value);

        Assert.Equal(expected, PostgresObservationStore.IsConformantProfileEnabled(configuration));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("enabled")]
    public void MalformedConfiguration_RejectsInsteadOfDisablingConformance(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PostgresObservationStore.IsConformantProfileEnabled(Configuration(value)));

        Assert.IsType<FormatException>(exception.InnerException);
        Assert.Contains("SensorThings:ConformantProfile", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SensorThings:ConformantProfile"] = value
        })
        .Build();
}
