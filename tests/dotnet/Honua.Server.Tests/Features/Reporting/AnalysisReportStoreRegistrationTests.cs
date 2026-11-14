// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Server.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Reporting;

[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.Security)]
public sealed class AnalysisReportStoreRegistrationTests
{
    [UnitTest]
    public void Registration_WithoutDatabase_PreservesLocalFallback()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddAnalysisReporting(configuration);
        services.AddAnalysisReporting(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAnalysisReportStore>().Should().BeOfType<Honua.Core.Features.Reporting.Services.InMemoryAnalysisReportStore>();
        services.Count(item => item.ServiceType == typeof(IAnalysisReportStore)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithDatabase_SelectsSharedStore()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=registration_only");
        services.AddSingleton(dataSource);
        services.AddSingleton(new JobStoreDatabase(dataSource));
        services.AddAnalysisReportingCore(configuration);
        services.AddAnalysisReporting(configuration);
        services.AddAnalysisReportingCore(configuration);
        services.AddAnalysisReporting(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAnalysisReportStore>().Should().BeOfType<PostgresAnalysisReportStore>();
        services.Count(item => item.ServiceType == typeof(IAnalysisReportStore)).Should().Be(1);
    }

    [UnitTest]
    public void Registration_WithCustomStore_PreservesOverride()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var custom = Substitute.For<IAnalysisReportStore>();
        services.AddSingleton(custom);
        services.AddAnalysisReporting(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAnalysisReportStore>().Should().BeSameAs(custom);
    }
}
