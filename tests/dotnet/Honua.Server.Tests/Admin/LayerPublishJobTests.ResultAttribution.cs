// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishJobTests
{
    [UnitTest]
    public async Task Executor_QueuedPublish_PreservesResultAttributionThroughStatusRefreshes()
    {
        const string statusUrl = "/api/v1/admin/connections/roads/layers/jobs/abc";
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.SubmitAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<LayerPublishRequest>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>()).Returns(new LayerPublishAdmission(
                LayerPublishAdmissionKind.Queued, null, "abc", 12, statusUrl));
        var policy = Policy();
        var request = Operation() with { VerifiedPublishResultAttribution = new("source-job", "source-artifact") };
        var payload = ServicePublishApprovalPayload.From(request, policy);
        var contract = ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload;
        var replay = JsonSerializer.Deserialize(JsonSerializer.Serialize(payload, contract), contract)!.ToOperationRequest();

        var handle = await Executor(jobs).SubmitAsync(replay, policy);

        Assert.Equal(OperationHandleStatus.Queued, handle.Status);
        Assert.Equal("source-job", handle.Result!.Details[PublishResultAttribution.SourceJobDetailKey]);
        jobs.GetAsync("abc", Arg.Any<CancellationToken>()).Returns(new LayerPublishJobProgress
        {
            OperationId = "abc",
            Status = ProgressStatus.Completed,
            RowsMaterialized = 12,
            RowsTotal = 12,
            LayerId = 7,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var status = await Executor(jobs).GetStatusAsync(handle);
            Assert.Equal(OperationHandleStatus.Completed, status.Status);
            Assert.Equal("source-job", status.Result!.Details[PublishResultAttribution.SourceJobDetailKey]);
            Assert.Equal("source-artifact", status.Result.Details[PublishResultAttribution.ArtifactDetailKey]);
            Assert.Equal(statusUrl, status.Result.Details["statusUrl"]);
            handle = handle with { Status = status.Status, Result = status.Result };
        }
    }
}
