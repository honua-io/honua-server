// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Server.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Verifies the Reporting feature registration honors the
/// <c>Reporting:Enabled</c> configuration flag so operators can fully disable
/// the reporting surface (services + endpoints) per the documented contract.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class ReportingServiceCollectionExtensionsTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void AddAnalysisReporting_WhenDefaultConfiguration_RegistersReportingServices()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddAnalysisReporting(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(IAnalysisReportService));
        services.Should().Contain(d => d.ServiceType == typeof(IAnalysisReportStore));
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void AddAnalysisReporting_WhenDisabled_RegistersNothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reporting:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAnalysisReporting(configuration);

        services.Should().NotContain(d => d.ServiceType == typeof(IAnalysisReportService));
        services.Should().NotContain(d => d.ServiceType == typeof(IAnalysisReportStore));
    }
}
