// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportExecutionJobQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetProgressAsync_JobCompletesDuringRead_ReturnsFinalProgress(bool expired)
    {
        var jobs = ImportExecutionTestJobs.Create();
        var store = Substitute.For<IUniversalProgressStore>();
        var manager = new GeoservicesImportJobManager(store,
            NullLogger<GeoservicesImportJobManager>.Instance, jobs, Substitute.For<IJobQueue>());
        var request = Request();
        var working = Initial(request) with { Status = GeoservicesImportStatus.InsertingFeatures };
        var completed = working with { Status = GeoservicesImportStatus.Completed, FeaturesProcessed = 42, DataCommitted = true };
        await manager.SubmitAsync(request, Initial(request));
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with { Status = ExecutionJobStatus.Succeeded })).Should().BeTrue();
        store.GetProgressAsync<GeoservicesImportProgress>(request.JobId!, Arg.Any<CancellationToken>())
            .Returns(working, expired ? null : completed);

        var result = await manager.ProgressStore.GetProgressAsync(request.JobId!);

        result.Should().BeEquivalentTo(expired ? null : completed);
    }
}
