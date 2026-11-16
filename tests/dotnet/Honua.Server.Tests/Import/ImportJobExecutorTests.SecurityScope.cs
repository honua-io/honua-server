// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    // The job worker opens a submitter scope around every executor (JobExecutionService); import
    // jobs carry no submitter, and a null-submitter scope refuses row-level-security reads.
    [UnitTest]
    public async Task GeoservicesJobExecutor_RunsTheImportOutsideTheSubmitterScope_SoReconciliationReadsEveryRow()
    {
        JobSecurityScopeState? observed = new(null);
        var importer = Substitute.For<IGeoservicesImportService>();
        importer.ImportLayerAsync(Arg.Any<GeoservicesImportRequest>(), Arg.Any<IProgress<GeoservicesImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = JobSecurityScope.Current;
                var request = call.Arg<GeoservicesImportRequest>();
                return GeoservicesImportResult.CreateSuccess(request.TableName, request.ServiceUrl, request.LayerId, featureCount: 1);
            });
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, jobManager, executor) = CreateGeoservicesExecutor(provider);
        var request = GeoservicesRequest("geoservices-scope");
        await jobManager.SubmitAsync(request,
            GeoservicesImportProgress.CreateInitial(request.JobId!, request.ServiceUrl, request.LayerId, request.TableName),
            CancellationToken.None);

        using (JobSecurityScope.Begin(submitter: null))
        {
            await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!, Substitute.For<IJobExecutionContext>(), CancellationToken.None);
        }

        observed.Should().BeNull();
    }

    [UnitTest]
    public async Task GeoServerJobExecutor_RunsTheImportOutsideTheSubmitterScope()
    {
        JobSecurityScopeState? observed = new(null);
        var importer = Substitute.For<IGeoServerImportService>();
        importer.ImportConfigurationAsync(Arg.Any<GeoServerImportRequest>(), Arg.Any<IProgress<GeoServerImportProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = JobSecurityScope.Current;
                var request = call.Arg<GeoServerImportRequest>();
                return GeoServerImportResult.CreateSuccess(request.GeoServerRestUrl, request.TargetHonuaUrl, wasDryRun: true);
            });
        using var provider = CreateGeoServerProvider(importer);
        var (jobs, jobManager, executor) = CreateGeoServerExecutor(provider);
        var request = GeoServerRequest("geoserver-scope");
        await jobManager.SubmitAsync(request,
            GeoServerImportProgress.CreateInitial(request.JobId!, request.GeoServerRestUrl, request.TargetHonuaUrl),
            CancellationToken.None);

        using (JobSecurityScope.Begin(submitter: null))
        {
            await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!, Substitute.For<IJobExecutionContext>(), CancellationToken.None);
        }

        observed.Should().BeNull();
    }
}
