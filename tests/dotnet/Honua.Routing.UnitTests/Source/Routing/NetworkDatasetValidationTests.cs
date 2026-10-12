// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Unit tests (no database) for network-dataset editing validation (#1882) and the
/// Postgres-only registration of the editing store.
/// </summary>
public sealed class NetworkDatasetValidationTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("maui-nui")]
    [InlineData("net_2")]
    public void TryValidateId_AcceptsLowercaseSlug(string id)
    {
        Assert.True(NetworkDatasetValidation.TryValidateId(id, out var error), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("1leading-digit")]
    [InlineData("trailing;--")]
    public void TryValidateId_RejectsInvalid(string id)
    {
        Assert.False(NetworkDatasetValidation.TryValidateId(id, out _));
    }

    [Theory]
    [InlineData("public.ways")]
    [InlineData("ways")]
    [InlineData("honua.network_edges")]
    public void TryValidateTableIdentifier_AcceptsSafeIdentifiers(string identifier)
    {
        Assert.True(NetworkDatasetValidation.TryValidateTableIdentifier(identifier, "edgeTable", out var error), error);
        Assert.True(NetworkDatasetValidation.IsValidTableIdentifier(identifier));
    }

    [Theory]
    [InlineData("public.ways; DROP TABLE x;--")]
    [InlineData("ways WHERE 1=1")]
    [InlineData("a.b.c")]
    [InlineData("\"Quoted\"")]
    [InlineData("")]
    public void TryValidateTableIdentifier_RejectsUnsafe(string identifier)
    {
        Assert.False(NetworkDatasetValidation.TryValidateTableIdentifier(identifier, "edgeTable", out _));
        Assert.False(NetworkDatasetValidation.IsValidTableIdentifier(identifier));
    }

    [Theory]
    [InlineData("cost", true)]
    [InlineData("walking_reverse_cost", true)]
    [InlineData("public.cost", false)]
    [InlineData("cost; DROP TABLE ways", false)]
    [InlineData("", false)]
    public void IsValidColumnIdentifier_RejectsInterpolationPayloads(string identifier, bool expected)
    {
        Assert.Equal(expected, NetworkDatasetValidation.IsValidColumnIdentifier(identifier));
    }

    [Theory]
    [InlineData("driving", true)]
    [InlineData("cargo-bike", true)]
    [InlineData("Walking", false)]
    [InlineData("walking;--", false)]
    public void IsValidTravelProfileName_AcceptsStableLowercaseNames(string name, bool expected)
    {
        Assert.Equal(expected, NetworkDatasetValidation.IsValidTravelProfileName(name));
    }

    [Fact]
    public void FilterProfilesByColumns_AdvertisesOnlyFullyBackedMappings()
    {
        var profiles = new[]
        {
            RoutingTravelProfile.Driving,
            new RoutingTravelProfile("walking", "walking_cost", "walking_reverse_cost"),
            new RoutingTravelProfile("cycling", "cycling_cost", "cycling_reverse_cost"),
        };
        IReadOnlySet<string> columns = new HashSet<string>(StringComparer.Ordinal)
        {
            "cost",
            "reverse_cost",
            "walking_cost",
            "walking_reverse_cost",
            "cycling_cost",
        };

        var result = NetworkDatasetRegistry.FilterProfilesByColumns(profiles, columns);

        Assert.Equal(new[] { "driving", "walking" }, result.Select(profile => profile.Name));
    }

    [Theory]
    [InlineData("smallint", null, true)]
    [InlineData("integer", null, true)]
    [InlineData("bigint", null, true)]
    [InlineData("numeric", null, true)]
    [InlineData("real", null, true)]
    [InlineData("double precision", null, true)]
    [InlineData("text", null, false)]
    [InlineData("boolean", null, false)]
    [InlineData("integer", "positive_integer", false)]
    public void IsPgRoutingNumericDataType_AcceptsOnlyPrimitiveNumericTypes(
        string dataType,
        string? domainName,
        bool expected)
    {
        Assert.Equal(
            expected,
            NetworkDatasetRegistry.IsPgRoutingNumericDataType(dataType, domainName));
    }

    [Fact]
    public void ParseTravelProfiles_EmptyList_FailsClosedWithoutDriving()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NetworkDatasetRegistry.ParseTravelProfiles("[]"));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("driving", exception.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTravelProfiles_MissingDriving_FailsClosed()
    {
        const string json =
            """[{"name":"walking","forwardCostColumn":"walking_cost","reverseCostColumn":"walking_reverse_cost"}]""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => NetworkDatasetRegistry.ParseTravelProfiles(json));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("driving", exception.InnerException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTravelProfiles_MalformedJson_NormalizesToConfigurationFailure()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => NetworkDatasetRegistry.ParseTravelProfiles("{not-json"));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
        Assert.DoesNotContain("{not-json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTravelProfiles_MissingProperty_NormalizesToConfigurationFailure()
    {
        const string json = """[{"name":"driving","forwardCostColumn":"cost"}]""";

        var exception = Assert.Throws<InvalidOperationException>(
            () => NetworkDatasetRegistry.ParseTravelProfiles(json));

        Assert.Contains("metadata is invalid", exception.Message, StringComparison.Ordinal);
        Assert.IsType<KeyNotFoundException>(exception.InnerException);
        Assert.DoesNotContain(json, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterProfilesByColumns_UnbackedDriving_FailsClosed()
    {
        var profiles = new[]
        {
            RoutingTravelProfile.Driving,
            new RoutingTravelProfile("walking", "walking_cost", "walking_reverse_cost"),
        };
        IReadOnlySet<string> numericColumns = new HashSet<string>(StringComparer.Ordinal)
        {
            "cost",
            "walking_cost",
            "walking_reverse_cost",
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => NetworkDatasetRegistry.FilterProfilesByColumns(profiles, numericColumns));

        Assert.Contains("fully backed 'driving'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4326, true)]
    [InlineData(3857, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void TryValidateSrid_BoundsSrid(int srid, bool expected)
    {
        Assert.Equal(expected, NetworkDatasetValidation.TryValidateSrid(srid, out _));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("building", true)]
    [InlineData("disabled", true)]
    [InlineData("archived", false)]
    [InlineData("", false)]
    public void TryValidateStatus_AcceptsKnownStatuses(string status, bool expected)
    {
        Assert.Equal(expected, NetworkDatasetValidation.TryValidateStatus(status, out _));
    }

    [Fact]
    public void AddRouting_RegistersStore_OnPostgres()
    {
        var services = BuildServices(("DataSource:Provider", "postgres"));
        Assert.Contains(services, d => d.ServiceType == typeof(INetworkDatasetStore));
    }

    [Fact]
    public void AddRouting_DoesNotRegisterStore_OnNonPostgres()
    {
        var services = BuildServices(("DataSource:Provider", "duckdb"));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(INetworkDatasetStore));
    }

    private static ServiceCollection BuildServices(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        var services = new ServiceCollection();
        services.AddRouting(configuration);
        return services;
    }
}
