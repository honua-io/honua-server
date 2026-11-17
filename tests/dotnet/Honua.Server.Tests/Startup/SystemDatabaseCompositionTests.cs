// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Startup;

[Trait("Category", "Unit")]
public sealed class SystemDatabaseCompositionTests
{
    private const string Connection = "Host=db;Database=honua;Username=honua";
    private const string MissingConnectionError = "ConnectionStrings__DefaultConnection is required";

    [Theory]
    [InlineData(null)]
    [InlineData("postgis")]
    [InlineData("postgresql")]
    public void IsConfigured_PostgreSqlPrimaryWithAConnection_IsASystemDatabase(string? provider)
        => SystemDatabaseComposition.IsConfigured(Build(provider, Connection)).Should().BeTrue();

    [Theory]
    [InlineData("postgis", null)]
    [InlineData("duckdb", Connection)]
    [InlineData("mysql", Connection)]
    [InlineData("mariadb", null)]
    public void IsConfigured_NoConnectionOrNonPostgreSqlPrimary_IsNotASystemDatabase(string provider, string? connection)
        => SystemDatabaseComposition.IsConfigured(Build(provider, connection)).Should().BeFalse();

    [Theory]
    [InlineData("duckdb")]
    [InlineData("mysql")]
    public void ProductionValidation_NonPostgreSqlPrimaryWithoutConnection_DoesNotRequireOne(string provider)
        => ValidateProduction(provider).Should().NotContain(error => error.Contains(MissingConnectionError));

    [Theory]
    [InlineData(null)]
    [InlineData("postgis")]
    public void ProductionValidation_PostgreSqlPrimaryWithoutConnection_StillRequiresOne(string? provider)
        => ValidateProduction(provider).Should().Contain(error => error.Contains(MissingConnectionError));

    private static List<string> ValidateProduction(string? provider)
        => ConfigurationValidationService.ValidateConfiguration(
            Build(provider, connection: null), NullLogger.Instance, isDevelopment: false);

    private static IConfiguration Build(string? provider, string? connection)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataSource:Provider"] = provider,
                ["ConnectionStrings:DefaultConnection"] = connection,
            })
            .Build();
}
