// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing.Cli.Publish;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

public sealed class WorkflowPackagePublishClientTests
{
    private const string PackageJson = """{"packageId":"my-flow","name":"My Flow","graph":{"schemaVersion":"workflow-package.v1","nodes":[]},"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}""";
    private const string VersionJson = """{"packageId":"my-flow","version":3,"schemaVersion":"workflow-package.v1","packageHash":"abc123","graph":{"schemaVersion":"workflow-package.v1","nodes":[]},"validation":{"isValid":true},"createdAt":"2026-01-01T00:00:00Z"}""";
    private const string ValidationJson = """{"isValid":true,"packageHash":"abc123"}""";
    private const string DryRunJson = """{"validation":{"isValid":true},"estimatedDurationSeconds":5,"estimatedCostWeight":1.5,"packageHash":"abc123"}""";
    private const string PublicationJson = """{"publicationId":"pub-1","packageId":"my-flow","packageVersion":3,"packageHash":"abc123","target":"ProcessEndpoint","eligibility":{"isValid":true},"createdAt":"2026-01-01T00:00:00Z"}""";

    private static SaveWorkflowPackageRequestDto SampleRequest()
        => new()
        {
            PackageId = "my-flow",
            Name = "My Flow",
            Graph = new WorkflowGraph { Nodes = [] }
        };

    private static (WorkflowPackagePublishClient Client, RecordingHttpMessageHandler Handler) CreateClient(
        Action<RecordingHttpMessageHandler> configure,
        string? apiKey = "secret-key",
        string? bearer = null)
    {
        var handler = new RecordingHttpMessageHandler();
        configure(handler);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        return (new WorkflowPackagePublishClient(http, apiKey, bearer), handler);
    }

    [Fact]
    public async Task SaveVersionAndPublish_WithoutPublish_CallsSaveThenVersionThenValidate()
    {
        var (client, handler) = CreateClient(h => h
            .RespondCreated("/workflow-packages", PackageJson)
            .RespondCreated("/versions", VersionJson)
            .RespondOk("/validate", ValidationJson));

        var result = await client.SaveVersionAndPublishAsync(
            SampleRequest(), validate: true, dryRun: false, publish: false, publishRequest: null);

        // Exact call sequence: save -> create version -> validate.
        Assert.Collection(
            handler.Requests,
            r => Assert.Equal(("POST", "/api/v1/console/workflow-packages"), (r.Method, r.Path)),
            r => Assert.Equal(("POST", "/api/v1/console/workflow-packages/my-flow/versions"), (r.Method, r.Path)),
            r => Assert.Equal(("POST", "/api/v1/console/workflow-packages/my-flow/versions/3/validate"), (r.Method, r.Path)));

        Assert.Equal("my-flow", result.Package.PackageId);
        Assert.Equal(3, result.Version!.Version);
        Assert.Equal("abc123", result.Version.PackageHash);
        Assert.True(result.Validation!.IsValid);
        Assert.Null(result.Publication);
    }

    [Fact]
    public async Task DryRunOnly_DoesNotCallPublishEndpoint()
    {
        var (client, handler) = CreateClient(h => h
            .RespondCreated("/workflow-packages", PackageJson)
            .RespondCreated("/versions", VersionJson)
            .RespondOk("/validate", ValidationJson)
            .RespondOk("/dry-run", DryRunJson));

        var result = await client.DryRunOnlyAsync(SampleRequest());

        Assert.NotNull(result.Validation);
        Assert.NotNull(result.DryRun);
        Assert.Null(result.Publication);
        // No request path ends with /publish.
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/publish", StringComparison.Ordinal));
        // Dry-run endpoint WAS hit.
        Assert.Contains(handler.Requests, r => r.Path.EndsWith("/dry-run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveVersionAndPublish_WithPublish_CallsPublishEndpointLast()
    {
        var (client, handler) = CreateClient(h => h
            .RespondCreated("/workflow-packages", PackageJson)
            .RespondCreated("/versions", VersionJson)
            .RespondOk("/validate", ValidationJson)
            .RespondOk("/publish", PublicationJson));

        var publishRequest = new PublishWorkflowPackageRequestDto
        {
            Target = WorkflowPublicationTarget.ProcessEndpoint,
            ProcessId = "geometry.buffer"
        };

        var result = await client.SaveVersionAndPublishAsync(
            SampleRequest(), validate: true, dryRun: false, publish: true, publishRequest);

        Assert.NotNull(result.Publication);
        Assert.Equal("pub-1", result.Publication!.PublicationId);
        Assert.Equal(
            "/api/v1/console/workflow-packages/my-flow/versions/3/publish",
            handler.Requests[^1].Path);
    }

    [Fact]
    public async Task SaveRequest_SerializesGraphAndProcessNode_WithCamelCaseAndStringEnums()
    {
        var (client, handler) = CreateClient(h => h
            .RespondCreated("/workflow-packages", PackageJson)
            .RespondCreated("/versions", VersionJson)
            .RespondOk("/validate", ValidationJson));

        var request = new SaveWorkflowPackageRequestDto
        {
            PackageId = "my-flow",
            Name = "My Flow",
            Graph = new WorkflowGraph
            {
                Nodes =
                [
                    new WorkflowNode { NodeId = "n1", NodeTypeId = "process:geometry.buffer" }
                ],
                Edges =
                [
                    new WorkflowEdge
                    {
                        SourceNodeId = "n1",
                        TargetNodeId = "n2",
                        Kind = WorkflowEdgeKind.Control
                    }
                ]
            }
        };

        await client.SaveVersionAndPublishAsync(
            request, validate: true, dryRun: false, publish: false, publishRequest: null);

        var saveBody = handler.Requests[0].Body!;
        // camelCase property names.
        Assert.Contains("\"packageId\":\"my-flow\"", saveBody, StringComparison.Ordinal);
        Assert.Contains("\"nodeTypeId\":\"process:geometry.buffer\"", saveBody, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":\"workflow-package.v1\"", saveBody, StringComparison.Ordinal);
        // Enum serialized as the string name, not a number.
        Assert.Contains("\"kind\":\"Control\"", saveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\":1", saveBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiKey_IsSentAsXApiKeyHeader()
    {
        var (client, handler) = CreateClient(
            h => h.RespondCreated("/workflow-packages", PackageJson).RespondCreated("/versions", VersionJson),
            apiKey: "secret-key");

        await client.SaveVersionAndPublishAsync(
            SampleRequest(), validate: false, dryRun: false, publish: false, publishRequest: null);

        Assert.Equal("secret-key", handler.Requests[0].ApiKeyHeader);
        Assert.Null(handler.Requests[0].AuthorizationHeader);
    }

    [Fact]
    public async Task BearerToken_IsSentAsAuthorizationHeader_AndTakesPrecedenceOverApiKey()
    {
        var (client, handler) = CreateClient(
            h => h.RespondCreated("/workflow-packages", PackageJson).RespondCreated("/versions", VersionJson),
            apiKey: "secret-key",
            bearer: "tok-123");

        await client.SaveVersionAndPublishAsync(
            SampleRequest(), validate: false, dryRun: false, publish: false, publishRequest: null);

        Assert.Equal("Bearer tok-123", handler.Requests[0].AuthorizationHeader);
        Assert.Null(handler.Requests[0].ApiKeyHeader);
    }

    [Fact]
    public async Task NonSuccessStatus_RaisesHttpException_WithServerMessage()
    {
        var (client, _) = CreateClient(h => h
            .Respond("/workflow-packages", HttpStatusCode.Unauthorized, "{\"message\":\"Admin authorization required.\"}"));

        var ex = await Assert.ThrowsAsync<GpPublishHttpException>(() =>
            client.SaveVersionAndPublishAsync(
                SampleRequest(), validate: false, dryRun: false, publish: false, publishRequest: null));

        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Admin authorization required", ex.Message, StringComparison.Ordinal);
    }
}
