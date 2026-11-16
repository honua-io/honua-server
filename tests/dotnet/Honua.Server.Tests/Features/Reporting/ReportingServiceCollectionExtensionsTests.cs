// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Server.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Reporting;

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
