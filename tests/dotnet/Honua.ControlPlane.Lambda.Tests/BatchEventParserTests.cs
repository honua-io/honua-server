// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane.Lambda;
using Xunit;

namespace Honua.ControlPlane.Lambda.Tests;

/// <summary>
/// Tests the event-deserialization contract: a canonical EventBridge "Batch Job State Change" event
/// JSON deserializes to the provider job id (<c>detail.jobId</c>) that the reconcile entrypoint
/// forwards, and malformed / non-matching / terminal payloads resolve to a clean no-op (null).
/// </summary>
public sealed class BatchEventParserTests
{
    // A representative AWS Batch "Batch Job State Change" EventBridge event (trimmed to the shape the
    // parser depends on, with extra detail fields present to prove unknown members are tolerated).
    private const string SampleBatchStateChangeEvent = """
    {
      "version": "0",
      "id": "c8f9c4b5-1f5b-4c2a-9b1e-1a2b3c4d5e6f",
      "detail-type": "Batch Job State Change",
      "source": "aws.batch",
      "account": "123456789012",
      "time": "2026-06-21T12:34:56Z",
      "region": "us-east-1",
      "resources": [
        "arn:aws:batch:us-east-1:123456789012:job/26c7b4e8-1234-4abc-9def-0123456789ab"
      ],
      "detail": {
        "jobArn": "arn:aws:batch:us-east-1:123456789012:job/26c7b4e8-1234-4abc-9def-0123456789ab",
        "jobName": "honua-gp-export",
        "jobId": "26c7b4e8-1234-4abc-9def-0123456789ab",
        "jobQueue": "arn:aws:batch:us-east-1:123456789012:job-queue/honua",
        "status": "SUCCEEDED",
        "createdAt": 1750000000000,
        "container": { "image": "honua/worker:latest" }
      }
    }
    """;

    [Fact]
    public void ExtractProviderOperationId_FromSampleBatchEventJson_ReturnsDetailJobId()
    {
        var providerId = BatchEventParser.ExtractProviderOperationId(SampleBatchStateChangeEvent);

        providerId.Should().Be("26c7b4e8-1234-4abc-9def-0123456789ab");
    }

    [Fact]
    public void ExtractProviderOperationId_NullOrBlankJson_ReturnsNull()
    {
        BatchEventParser.ExtractProviderOperationId((string?)null).Should().BeNull();
        BatchEventParser.ExtractProviderOperationId("   ").Should().BeNull();
    }

    [Fact]
    public void ExtractProviderOperationId_MalformedJson_ReturnsNullAndDoesNotThrow()
    {
        BatchEventParser.ExtractProviderOperationId("{ not json").Should().BeNull();
    }

    [Fact]
    public void ExtractProviderOperationId_WrongDetailType_ReturnsNull()
    {
        const string wrongType = """
        { "source": "aws.batch", "detail-type": "EC2 Instance State-change Notification",
          "detail": { "jobId": "should-be-ignored" } }
        """;

        BatchEventParser.ExtractProviderOperationId(wrongType).Should().BeNull();
    }

    [Fact]
    public void ExtractProviderOperationId_WrongSource_ReturnsNull()
    {
        const string wrongSource = """
        { "source": "aws.ec2", "detail-type": "Batch Job State Change",
          "detail": { "jobId": "should-be-ignored" } }
        """;

        BatchEventParser.ExtractProviderOperationId(wrongSource).Should().BeNull();
    }

    [Fact]
    public void ExtractProviderOperationId_MissingDetailOrJobId_ReturnsNull()
    {
        BatchEventParser.ExtractProviderOperationId(
            """{ "source": "aws.batch", "detail-type": "Batch Job State Change" }""")
            .Should().BeNull();

        BatchEventParser.ExtractProviderOperationId(
            """{ "source": "aws.batch", "detail-type": "Batch Job State Change", "detail": { "status": "RUNNING" } }""")
            .Should().BeNull();
    }

    [Fact]
    public void ExtractProviderOperationId_FromTypedEvent_ReturnsJobId()
    {
        var evt = new BatchJobStateChangeEvent
        {
            Source = BatchEventParser.ExpectedSource,
            DetailType = BatchEventParser.ExpectedDetailType,
            Detail = new BatchJobStateChangeDetail { JobId = "batch-job-xyz", Status = "RUNNING" },
        };

        BatchEventParser.ExtractProviderOperationId(evt).Should().Be("batch-job-xyz");
    }
}
