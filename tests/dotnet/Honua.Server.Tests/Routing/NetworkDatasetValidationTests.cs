// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
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
