// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing.Cli.Publish;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

public sealed class GpPublishCommandTests : IDisposable
{
    private readonly string _fixtureRoot = Path.Combine(
        Path.GetTempPath(),
        "gp-publish-cmd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    private const string PackageJson = """{"packageId":"my-flow","name":"My Flow","graph":{"schemaVersion":"workflow-package.v1","nodes":[]},"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z"}""";
    private const string VersionJson = """{"packageId":"my-flow","version":7,"schemaVersion":"workflow-package.v1","packageHash":"hash-7","graph":{"schemaVersion":"workflow-package.v1","nodes":[]},"validation":{"isValid":true},"createdAt":"2026-01-01T00:00:00Z"}""";
    private const string ValidationJson = """{"isValid":true,"packageHash":"hash-7"}""";
    private const string DryRunJson = """{"validation":{"isValid":true},"estimatedDurationSeconds":4,"estimatedCostWeight":2.0,"packageHash":"hash-7"}""";
    private const string PublicationJson = """{"publicationId":"pub-9","packageId":"my-flow","packageVersion":7,"packageHash":"hash-7","target":"ProcessEndpoint","endpointPath":"/rest/.../GPServer","eligibility":{"isValid":true},"createdAt":"2026-01-01T00:00:00Z"}""";

    private sealed class StubCatalog(params ProcessDefinition[] processes) : IProcessCatalog
    {
        public ProcessDefinition? GetProcess(string processId)
            => processes.FirstOrDefault(p => p.ProcessId == processId);

        public IReadOnlyList<ProcessDefinition> ListProcesses() => processes;

        public IReadOnlyList<ProcessDefinition> GetProcessesByCategory(string category)
            => processes.Where(p => p.Category == category).ToArray();
    }

    private static ProcessDefinition BufferProcess()
        => new()
        {
            ProcessId = "geometry.buffer",
            Title = "Buffer",
            Description = "Buffer",
            Category = "geometry",
            Parameters = [],
            OutputArtifactKinds = [ArtifactKind.FeatureLayer]
        };

    private void WriteFixture(string id, string json)
    {
        Directory.CreateDirectory(Path.Combine(_fixtureRoot, id));
        File.WriteAllText(Path.Combine(_fixtureRoot, id, "workflow.json"), json);
    }

    private async Task<(int Exit, string Out, string Err, RecordingHttpMessageHandler? Handler)> RunAsync(
        string[] args,
        Action<RecordingHttpMessageHandler>? configure = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var processIds = new HashSet<string>(StringComparer.Ordinal) { "geometry.buffer" };
        var catalog = new StubCatalog(BufferProcess());
        RecordingHttpMessageHandler? handler = null;

        WorkflowPackagePublishClient Factory(GpPublishOptions options)
        {
            handler = new RecordingHttpMessageHandler();
            configure?.Invoke(handler);
            var http = new HttpClient(handler) { BaseAddress = new Uri(options.Server) };
            return new WorkflowPackagePublishClient(http, options.ApiKey, options.BearerToken);
        }

        // Append the temp fixture root via the public option by injecting it through args is not
        // supported, so we resolve it directly: tests that need fixtures pass --file instead, and
        // the fixtureRoot default is exercised by GpWorkflowSourceTests. For command tests we use
        // --file or a registered process id.
        var exit = await GpPublishCommand.RunAsync(args, processIds, catalog, Factory, output, error);
        return (exit, output.ToString(), error.ToString(), handler);
    }

    [Fact]
    public async Task Run_CodeProcessWithoutWrapFlag_PrintsImageGuidance_AndExitsZero()
    {
        var (exit, output, _, handler) = await RunAsync(["geometry.buffer"]);

        Assert.Equal(0, exit);
        Assert.Contains("not published", output, StringComparison.Ordinal);
        Assert.Contains("server image", output, StringComparison.Ordinal);
        // No HTTP calls were made for a non-publishable code process.
        Assert.Null(handler);
    }

    [Fact]
    public async Task Run_UnknownId_ExitsTwo()
    {
        var (exit, output, _, handler) = await RunAsync(["totally-unknown"]);

        Assert.Equal(2, exit);
        Assert.Contains("not published", output, StringComparison.Ordinal);
        Assert.Null(handler);
    }

    [Fact]
    public async Task Run_ProcessNode_Publish_DrivesFullSequence_AndReportsNextStep()
    {
        var (exit, output, _, handler) = await RunAsync(
            ["geometry.buffer", "--as-process-node", "--publish", "--api-key", "k"],
            h => h
                .RespondCreated("/workflow-packages", PackageJson)
                .RespondCreated("/versions", VersionJson)
                .RespondOk("/validate", ValidationJson)
                .RespondOk("/publish", PublicationJson));

        Assert.Equal(0, exit);
        Assert.Contains("version   : 7", output, StringComparison.Ordinal);
        Assert.Contains("hash      : hash-7", output, StringComparison.Ordinal);
        Assert.Contains("published : pub-9", output, StringComparison.Ordinal);
        Assert.Contains("GitOps", output, StringComparison.Ordinal);
        Assert.Contains("does NOT deploy across envs", output, StringComparison.Ordinal);

        Assert.NotNull(handler);
        Assert.Equal("k", handler!.Requests[0].ApiKeyHeader);
        Assert.EndsWith("/publish", handler.Requests[^1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_DryRun_DoesNotPublish_AndSaysNothingWasPublished()
    {
        var (exit, output, _, handler) = await RunAsync(
            ["geometry.buffer", "--as-process-node", "--dry-run"],
            h => h
                .RespondCreated("/workflow-packages", PackageJson)
                .RespondCreated("/versions", VersionJson)
                .RespondOk("/validate", ValidationJson)
                .RespondOk("/dry-run", DryRunJson));

        Assert.Equal(0, exit);
        Assert.Contains("dry-run", output, StringComparison.Ordinal);
        Assert.Contains("Nothing was published", output, StringComparison.Ordinal);
        Assert.NotNull(handler);
        Assert.DoesNotContain(handler!.Requests, r => r.Path.EndsWith("/publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_HttpError_PrintsError_AndExitsOne()
    {
        var (exit, _, err, _) = await RunAsync(
            ["geometry.buffer", "--as-process-node"],
            h => h.Respond("/workflow-packages", System.Net.HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}"));

        Assert.Equal(1, exit);
        Assert.Contains("error", err, StringComparison.Ordinal);
        Assert.Contains("403", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseOptions_DefaultsServerToLocalQuickstart()
    {
        var options = GpPublishCommand.ParseOptions(["my-flow"]);

        Assert.Equal("my-flow", options.Id);
        Assert.Equal("http://localhost:8080", options.Server);
    }

    [Fact]
    public void ParseOptions_DryRunAndPublish_AreMutuallyExclusive()
    {
        Assert.Throws<GpCliUsageException>(() =>
            GpPublishCommand.ParseOptions(["my-flow", "--dry-run", "--publish"]));
    }

    [Fact]
    public void ParseOptions_ReadsServerApiKeyAndFlags()
    {
        var options = GpPublishCommand.ParseOptions(
            ["my-flow", "--server", "http://h:9", "--api-key", "abc", "--message", "msg", "--publish", "--as-process-node"]);

        Assert.Equal("http://h:9", options.Server);
        Assert.Equal("abc", options.ApiKey);
        Assert.Equal("msg", options.Message);
        Assert.True(options.Publish);
        Assert.True(options.AsProcessNode);
    }

    [Fact]
    public void ParseOptions_MissingId_Throws()
    {
        Assert.Throws<GpCliUsageException>(() => GpPublishCommand.ParseOptions([]));
    }
}
