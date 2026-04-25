// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Abstractions;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Exercises the server-side report orchestrator: authorization delegation,
/// render caching, and not-found / failed-precondition propagation.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class AnalysisReportServiceTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetReportAsync_DelegatesAuthorizationAndReturnsEnvelope()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync("job-1", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ReportingFixtures.BufferAggregatePackage());

        var service = CreateService(jobService, out _);

        var report = await service.GetReportAsync(
            "job-1",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "op")], "Test")),
            CancellationToken.None);

        jobService.Received(1).EnsureCallerAuthorized(
            Arg.Any<ClaimsPrincipal>(),
            OperatorResourceType.Job,
            OperatorOperation.Read);

        report.JobId.Should().Be("job-1");
        report.ReportContractVersion.Should().Be(ReportingConstants.ContractVersionV1);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetReportAsync_NonTerminalJob_PropagatesFailedPrecondition()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync("job-x", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns<Task<AnalysisResultPackage>>(_ => throw new GeoprocessingPreconditionFailedException("not terminal"));

        var service = CreateService(jobService, out _);

        var act = async () => await service.GetReportAsync(
            "job-x",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "op")], "Test")),
            CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetReportAsync_CachesResultsPerResultPackage()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var package = ReportingFixtures.BufferAggregatePackage();
        jobService.GetJobResultsAsync("job-cache", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(package);

        var service = CreateService(jobService, out var store);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "op")], "Test"));

        var first = await service.GetReportAsync("job-cache", principal, CancellationToken.None);
        var second = await service.GetReportAsync("job-cache", principal, CancellationToken.None);

        first.ReportId.Should().Be(second.ReportId);
        var cached = await store.TryGetAsync("job-cache", ReportingConstants.ContractVersionV1, CancellationToken.None);
        cached.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetRenderedAsync_RendersMarkdownAndCachesBody()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        jobService.GetJobResultsAsync("job-md", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(ReportingFixtures.BufferAggregatePackage());

        var service = CreateService(jobService, out _);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "op")], "Test"));

        var first = await service.GetRenderedAsync("job-md", AnalysisReportFormat.Markdown, principal, CancellationToken.None);
        var second = await service.GetRenderedAsync("job-md", AnalysisReportFormat.Markdown, principal, CancellationToken.None);

        first.Body.Should().StartWith("# Buffered places");
        first.Body.Should().Be(second.Body);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GetReportAsync_ReplacesStaleCacheWhenResultPackageIdChanges()
    {
        var jobService = Substitute.For<IGeoprocessingJobService>();
        var first = ReportingFixtures.BufferAggregatePackage();
        var second = first with { ResultPackageId = "pkg-new" };
        jobService.GetJobResultsAsync("job-evict", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(first, second);

        var service = CreateService(jobService, out var store);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "op")], "Test"));

        var before = await service.GetReportAsync("job-evict", principal, CancellationToken.None);
        var after = await service.GetReportAsync("job-evict", principal, CancellationToken.None);

        before.ResultPackageId.Should().Be("pkg-buffer");
        after.ResultPackageId.Should().Be("pkg-new");
        after.ReportId.Should().NotBe(before.ReportId);
    }

    private static AnalysisReportService CreateService(IGeoprocessingJobService jobService, out IAnalysisReportStore store)
    {
        var templates = ReportingFixtures.CreateBuilder();
        var builder = templates;
        var storeImpl = new InMemoryAnalysisReportStore();
        store = storeImpl;

        IAnalysisReportRenderer[] renderers =
        [
            new MarkdownReportRenderer(),
            new HtmlReportRenderer()
        ];

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(new ReportingConfiguration());

        return new AnalysisReportService(
            jobService,
            builder,
            storeImpl,
            renderers,
            memoryCache,
            options,
            NullLogger<AnalysisReportService>.Instance);
    }
}
