// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.ServiceDefaults;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Protocol(TestProtocols.Mcp)]
[Collection("McpTelemetry")]
public sealed class McpDispatcherTelemetryTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();
    private readonly IPublishedServiceStore _services = Substitute.For<IPublishedServiceStore>();
    private readonly IDeploymentStore _deployments = Substitute.For<IDeploymentStore>();

    [UnitTest]
    [Endpoint("POST /mcp initialize")]
    public async Task DispatchAsync_Initialize_TagsActivityWithProtocolAndMethod()
    {
        using var activity = new Activity("test-initialize").Start();
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"i-1\""),
            Method = "initialize",
            Params = McpTestFactory.ParseJson("""
                {"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"t","version":"1"}}
                """)
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        activity.GetTagItem(HonuaTelemetry.Tags.Protocol).Should().Be(HonuaTelemetry.Protocols.Mcp);
        activity.GetTagItem(HonuaTelemetry.Tags.Operation).Should().Be("initialize");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/list")]
    public async Task DispatchAsync_ToolsList_TagsActivityWithProtocolAndMethod()
    {
        using var activity = new Activity("test-tools-list").Start();
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"tl-1\""),
            Method = "tools/list"
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        activity.GetTagItem(HonuaTelemetry.Tags.Protocol).Should().Be(HonuaTelemetry.Protocols.Mcp);
        activity.GetTagItem(HonuaTelemetry.Tags.Operation).Should().Be("tools/list");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/list")]
    public async Task DispatchAsync_ResourcesList_TagsActivityWithProtocolAndMethod()
    {
        using var activity = new Activity("test-resources-list").Start();
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"rl-1\""),
            Method = "resources/list"
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        activity.GetTagItem(HonuaTelemetry.Tags.Protocol).Should().Be(HonuaTelemetry.Protocols.Mcp);
        activity.GetTagItem(HonuaTelemetry.Tags.Operation).Should().Be("resources/list");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/templates/list")]
    public async Task DispatchAsync_ResourceTemplatesList_TagsActivityWithProtocolAndMethod()
    {
        using var activity = new Activity("test-resource-templates-list").Start();
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"rtl-1\""),
            Method = "resources/templates/list"
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        activity.GetTagItem(HonuaTelemetry.Tags.Protocol).Should().Be(HonuaTelemetry.Protocols.Mcp);
        activity.GetTagItem(HonuaTelemetry.Tags.Operation).Should().Be("resources/templates/list");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call")]
    public async Task DispatchAsync_AnonymousToolsCall_EmitsCounterWithUnknownSentinels()
    {
        var samples = new List<MeasurementSample>();
        using var listener = CreateListener(
            "honua.mcp.tool.call",
            tags => GetTagString(tags, "tool_name") == McpTelemetry.UnknownToolName,
            samples);

        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"tc-1\""),
            Method = "tools/call",
            Params = McpTestFactory.ParseJson("""{"name":"honua_validate_plan","arguments":{}}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        samples.Should().ContainSingle();
        var tags = samples[0].Tags;
        GetTagString(tags, "tool_name").Should().Be(McpTelemetry.UnknownToolName);
        GetTagString(tags, "status").Should().Be(McpTelemetry.Status.Error);
        GetTagString(tags, "workflow_family").Should().Be(McpTelemetry.WorkflowFamily.Unknown);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read")]
    public async Task DispatchAsync_AnonymousResourcesRead_EmitsCounterWithUnknownFamily()
    {
        var samples = new List<MeasurementSample>();
        using var listener = CreateListener(
            "honua.mcp.resource.read",
            tags => GetTagString(tags, "resource_family") == McpTelemetry.ResourceFamily.Unknown,
            samples);

        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"rr-1\""),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("""{"uri":"honua://jobs/job-1"}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AnonymousHttpContext(), request, CancellationToken.None);

        samples.Should().ContainSingle();
        var tags = samples[0].Tags;
        GetTagString(tags, "resource_family").Should().Be(McpTelemetry.ResourceFamily.Unknown);
        GetTagString(tags, "status").Should().Be(McpTelemetry.Status.Error);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://published-services")]
    public async Task DispatchAsync_PromotionIndex_PublishedServicesRoot_EmitsPublishedServicesFamily()
    {
        _services.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PublishedServiceRecord>());
        _deployments.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Deployment>());

        var samples = new List<MeasurementSample>();
        using var listener = CreateListener(
            "honua.mcp.resource.read",
            tags => GetTagString(tags, "resource_family") == McpTelemetry.ResourceFamily.PublishedServices,
            samples);

        var surface = BuildPromotionSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"ps-1\""),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("""{"uri":"honua://published-services"}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        samples.Should().ContainSingle();
        var tags = samples[0].Tags;
        GetTagString(tags, "resource_family").Should().Be(McpTelemetry.ResourceFamily.PublishedServices);
        GetTagString(tags, "status").Should().Be(McpTelemetry.Status.Ok);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments")]
    public async Task DispatchAsync_PromotionIndex_DeploymentsRoot_EmitsDeploymentsFamily()
    {
        _services.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PublishedServiceRecord>());
        _deployments.ListActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Deployment>());

        var samples = new List<MeasurementSample>();
        using var listener = CreateListener(
            "honua.mcp.resource.read",
            tags => GetTagString(tags, "resource_family") == McpTelemetry.ResourceFamily.Deployments,
            samples);

        var surface = BuildPromotionSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"dep-1\""),
            Method = "resources/read",
            Params = McpTestFactory.ParseJson("""{"uri":"honua://deployments"}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        samples.Should().ContainSingle();
        var tags = samples[0].Tags;
        GetTagString(tags, "resource_family").Should().Be(McpTelemetry.ResourceFamily.Deployments);
        GetTagString(tags, "status").Should().Be(McpTelemetry.Status.Ok);
    }

    [UnitTest]
    [Endpoint("POST /mcp")]
    public async Task DispatchAsync_Ping_ReturnsEmptyObject()
    {
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("1"),
            Method = "ping"
        };

        var response = await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        response.Result.Should().NotBeNull();
        response.Result!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        response.Result!.Value.GetRawText().Should().Be("{}");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call")]
    public async Task DispatchAsync_ToolCall_RecordsToolExecutionDurationHistogram()
    {
        var durationSamples = new List<DoubleMeasurementSample>();
        using var listener = CreateDurationListener(durationSamples);
        var surface = BuildSurface();
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"dur-1\""),
            Method = "tools/call",
            Params = McpTestFactory.ParseJson("""{"name":"honua_cancel_job","arguments":{}}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        var recorded = durationSamples.Single(sample =>
            GetTagString(sample.Tags, "tool") == "honua_cancel_job");
        recorded.Value.Should().BeGreaterThanOrEqualTo(0);
        GetTagString(recorded.Tags, "outcome").Should().Be(McpTelemetry.Status.Error);
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call")]
    public async Task DispatchAsync_ToolReturningErrorResult_RecordsErrorOutcomeDuration()
    {
        var durationSamples = new List<DoubleMeasurementSample>();
        using var listener = CreateDurationListener(durationSamples);
        var errorTool = CreateErrorReturningTool("honua_returned_error_tool");
        var surface = new McpDataAccessSurface([errorTool], [], NullLogger<McpDataAccessSurface>.Instance);
        var request = new McpJsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = McpTestFactory.ParseJson("\"dur-err-1\""),
            Method = "tools/call",
            Params = McpTestFactory.ParseJson("""{"name":"honua_returned_error_tool","arguments":{}}""")
        };

        await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(), request, CancellationToken.None);

        var recorded = durationSamples.Single(sample =>
            GetTagString(sample.Tags, "tool") == "honua_returned_error_tool");
        GetTagString(recorded.Tags, "outcome").Should().Be(McpTelemetry.Status.Error);
    }

    private static IMcpTool CreateErrorReturningTool(string name)
    {
        var tool = Substitute.For<IMcpTool>();
        tool.Name.Returns(name);
        tool.WorkflowFamily.Returns(McpTelemetry.WorkflowFamily.Execution);
        tool.Describe().Returns(new McpToolDescriptor
        {
            Name = name,
            InputSchema = McpTestFactory.ParseJson("""{"type":"object","properties":{}}"""),
        });
        tool.InvokeAsync(Arg.Any<Microsoft.AspNetCore.Http.HttpContext>(), Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolsCallResult { IsError = true, Content = [] });
        return tool;
    }

    private static MeterListener CreateDurationListener(List<DoubleMeasurementSample> samples)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName
                    && instrument.Name == "honua.mcp.tool.duration")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            lock (samples)
            {
                samples.Add(new DoubleMeasurementSample(measurement, tags.ToArray()));
            }
        });
        listener.Start();
        return listener;
    }

    private static MeterListener CreateListener(
        string instrumentName,
        Func<KeyValuePair<string, object?>[], bool> filter,
        List<MeasurementSample> samples)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == HonuaTelemetry.ServiceName
                    && instrument.Name == instrumentName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            var snapshot = tags.ToArray();
            if (filter(snapshot))
            {
                lock (samples)
                {
                    samples.Add(new MeasurementSample(measurement, snapshot));
                }
            }
        });
        listener.Start();
        return listener;
    }

    private static string? GetTagString(KeyValuePair<string, object?>[] tags, string name)
        => tags.Where(tag => tag.Key == name).Select(tag => tag.Value as string).FirstOrDefault();

    private McpDataAccessSurface BuildSurface()
    {
        var tools = new IMcpTool[]
        {
            new ValidatePlanTool(_jobService, NullLogger<ValidatePlanTool>.Instance),
            new DryRunPlanTool(_jobService, NullLogger<DryRunPlanTool>.Instance),
            new ExecutePlanTool(_jobService, NullLogger<ExecutePlanTool>.Instance),
            new CancelJobTool(_jobService, NullLogger<CancelJobTool>.Instance)
        };
        var resources = new IMcpResource[]
        {
            new JobStatusResource(_jobService, NullLogger<JobStatusResource>.Instance),
            new JobResultsResource(_jobService, NullLogger<JobResultsResource>.Instance),
            new WorkspaceResource(_jobService, NullLogger<WorkspaceResource>.Instance),
            new ProcessCatalogResource(_jobService, NullLogger<ProcessCatalogResource>.Instance)
        };
        return new McpDataAccessSurface(tools, resources, NullLogger<McpDataAccessSurface>.Instance);
    }

    private McpDataAccessSurface BuildPromotionSurface()
    {
        var tools = Array.Empty<IMcpTool>();
        var resources = new IMcpResource[]
        {
            new PromotionSurfaceIndexResource(
                _services, _deployments, _jobService,
                NullLogger<PromotionSurfaceIndexResource>.Instance)
        };
        return new McpDataAccessSurface(tools, resources, NullLogger<McpDataAccessSurface>.Instance);
    }

    private readonly record struct MeasurementSample(long Value, KeyValuePair<string, object?>[] Tags);

    private readonly record struct DoubleMeasurementSample(double Value, KeyValuePair<string, object?>[] Tags);
}
