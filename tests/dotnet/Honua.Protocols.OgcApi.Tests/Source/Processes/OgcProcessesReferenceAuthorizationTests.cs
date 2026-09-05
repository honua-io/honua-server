// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Processes;
using Honua.Protocols.Ogc.Api.Processes.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes;

[Trait("Tier", "Fast")]
public sealed class OgcProcessesReferenceAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownLayerDenied_SharedAuthorizationPreventsEveryHttpRequest(bool dataUri)
    {
        using var fixture = new AuthorizationFixture(allowLayers: false);
        var inputs = "{\"layerId\":" + (dataUri ? "{\"href\":\"data:text/plain,7\"}" : "7")
            + ",\"tolerance\":{\"href\":\"https://93.184.216.34/payload\"}}";
        var action = () => fixture.NormalizeAsync("generalization.simplify-layer", inputs);
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task MutatingTierDenied_PreventsRemoteIdentifierLookup()
    {
        using var fixture = new AuthorizationFixture(allowMutating: false);
        var action = () => fixture.NormalizeAsync("import.dataset",
            """{"rasterLayerId":{"href":"https://93.184.216.34/identifier"},"sourcePath":{"href":"https://93.184.216.34/payload"}}""");
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        fixture.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteLayerSelector_AuthorizesIdentifierBeforePayload(bool allowLayers)
    {
        using var fixture = new AuthorizationFixture(allowLayers: allowLayers);
        var action = () => fixture.NormalizeAsync("generalization.simplify-layer",
            """{"layerId":{"href":"https://93.184.216.34/identifier"},"tolerance":{"href":"https://93.184.216.34/payload"}}""");
        if (allowLayers)
        {
            var result = await action();
            result.Error.Should().BeNull();
            result.AuthorizedPlan!.Steps.Single().Inputs["layerId"].Should().Be("7");
            fixture.Requests.Should().Equal("/identifier", "/payload");
        }
        else
        {
            await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
            fixture.Requests.Should().Equal("/identifier");
        }
    }

    [Fact]
    public async Task RemoteSelector_RejectsOversizedIdentifierBeforePayload()
    {
        using var fixture = new AuthorizationFixture();
        fixture.Identifier = new string('7', 4097);
        var result = await fixture.NormalizeAsync("generalization.simplify-layer",
            """{"layerId":{"href":"https://93.184.216.34/identifier"},"tolerance":{"href":"https://93.184.216.34/payload"}}""");
        result.Request.Should().BeNull();
        fixture.Requests.Should().Equal("/identifier");
    }

    [Fact]
    public async Task RegisteredRasterDenied_ResolvesOwnershipWithoutDownloadingInputsOrRasterBytes()
    {
        using var fixture = new AuthorizationFixture(allowLayers: false);
        var action = () => fixture.NormalizeAsync("surface.slope",
            """{"rasterId":"123","units":{"href":"https://93.184.216.34/payload"}}""");
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        fixture.Requests.Should().BeEmpty();
        await fixture.RasterResolver.Received(1).ResolveLayerIdAsync(
            Arg.Is<RasterSourceReference>(reference => reference.RasterId == 123), Arg.Any<CancellationToken>());
        await fixture.RasterResolver.DidNotReceive().ResolveAsync(
            Arg.Any<RasterSourceReference>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichmentDatasetDenied_PreventsPayloadDownload()
    {
        using var fixture = new AuthorizationFixture(allowLayers: false);
        var action = () => fixture.NormalizeAsync("enrichment.enrich",
            """{"datasetId":"boundaries","input":{"href":"https://93.184.216.34/features","type":"application/geo+json"}}""");
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        fixture.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnrichmentDatasetChangesDuringDownload_PreservedBindingRejectsReauthorization()
    {
        using var fixture = new AuthorizationFixture();
        fixture.OnPayload = () => fixture.DatasetLayerId = 9;
        var action = () => fixture.NormalizeAsync("enrichment.enrich",
            """{"datasetId":"boundaries","input":{"href":"https://93.184.216.34/features","type":"application/geo+json"}}""");
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        fixture.Requests.Should().Equal("/features");
    }

    [Fact]
    public async Task AuthorizedDatasetBinding_IsRetainedInCanonicalSubmissionAndRejectsLaterRebinding()
    {
        using var fixture = new AuthorizationFixture();
        var result = await fixture.NormalizeAsync("enrichment.enrich",
            """{"datasetId":"boundaries","input":{"href":"https://93.184.216.34/features","type":"application/geo+json"}}""");
        var authorized = result.AuthorizedPlan!;
        authorized.Steps.Single().Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput].Should().Be("8");
        var canonical = authorized with
        {
            Steps = [authorized.Steps.Single() with
            {
                Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["datasetId"] = "boundaries",
                    ["input"] = "data:application/geo+json;base64,eyJ0eXBlIjoiRmVhdHVyZUNvbGxlY3Rpb24iLCJmZWF0dXJlcyI6W119"
                }
            }]
        };
        var submission = ProcessEndpoints.PreserveReferenceAuthorization(canonical, authorized);
        submission.Steps.Single().Inputs["input"].Should().Be(canonical.Steps.Single().Inputs["input"]);
        submission.Steps.Single().Inputs[EnrichmentJobExecutor.AuthorizedDatasetLayerInput].Should().Be("8");
        fixture.DatasetLayerId = 9;
        var action = () => fixture.Service.EnsurePlanExecutionTierAuthorizedAsync(submission, fixture.Principal);
        await action.Should().ThrowAsync<GeoprocessingAuthorizationException>(
            "both layers are readable, but the requester authorized the original dataset binding");
    }

    private sealed class AuthorizationFixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HttpContextAccessor _accessor = new();
        private readonly BuiltInProcessCatalog _catalog = new();
        private readonly HttpClient _client;
        private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
        public ClaimsPrincipal Principal { get; } = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "analyst"), new Claim(ClaimTypes.Role, "reader")], "test"));
        public GeoprocessingJobService Service { get; }
        public IGeoprocessingRasterSourceResolver RasterResolver { get; } = Substitute.For<IGeoprocessingRasterSourceResolver>();
        public List<string> Requests { get; } = [];
        public int DatasetLayerId { get; set; } = 8;
        public string Identifier { get; set; } = "7";
        public Action? OnPayload { get; set; }

        public AuthorizationFixture(bool allowLayers = true, bool allowMutating = true)
        {
            var graph = new MetadataV2Graph
            {
                Revision = 1,
                Environment = "test",
                Services = [new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "test" },
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
                }],
                Resources = new[] { 7, 8, 9 }.Select(id => new MetadataV2Resource
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = $"resource-{id}", Name = $"layer-{id}" },
                    AccessPolicy = new AccessPolicy { AllowedRoles = [allowLayers ? "reader" : "denied"] },
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
                }).ToArray(),
                Publications = new[] { 7, 8, 9 }.Select(id => new MetadataV2Publication
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = $"publication-{id}", Name = $"layer-{id}" },
                    ServiceId = "service",
                    ResourceId = $"resource-{id}",
                    LayerIndex = id,
                    IsPrimary = true,
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
                }).ToArray()
            };
            var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
            graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
                new MetadataV2GraphSnapshot(graph, "reference-tests", DateTimeOffset.UnixEpoch));
            var datasetResolver = Substitute.For<IEnrichmentDatasetResolver>();
            datasetResolver.ResolveAsync("boundaries", Arg.Any<CancellationToken>()).Returns(_ =>
                new EnrichmentDatasetDefinition("boundaries", "Boundaries", "boundary", DatasetLayerId,
                    "intersects", null, ["name"], null, HonuaEdition.Pro, "config"));
            var services = new ServiceCollection();
            services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
            services.AddSingleton(graphProvider);
            services.AddSingleton(datasetResolver);
            _services = services.BuildServiceProvider();
            _accessor.HttpContext = new DefaultHttpContext { RequestServices = _services, User = Principal };
            var evaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
            evaluator.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(),
                Arg.Any<CancellationToken>()).Returns(call =>
                    !allowMutating && call.Arg<OperatorAuthorizationRequest>().Operation == OperatorOperation.ExecuteMutatingProcess
                        ? AccessDecision.Forbidden() : AccessDecision.Allowed());
            var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
            options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
            RasterResolver.ResolveLayerIdAsync(Arg.Any<RasterSourceReference>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<RasterSourceReference>().LayerId is null or 7
                    ? RasterSourceLayerResolution.Success(7) : RasterSourceLayerResolution.NotFound());
            Service = new GeoprocessingJobService(
                Substitute.For<IUniversalProgressStore>(), [], evaluator,
                Substitute.For<IOperatorApprovalEvaluator>(), _catalog,
                NullLogger<GeoprocessingJobService>.Instance, options,
                rasterSourceResolver: RasterResolver,
                layerAccessAuthorizer: new LayerAccessAuthorizer(_accessor, _services.GetRequiredService<IServiceScopeFactory>()),
                httpContextAccessor: _accessor);
            _client = new HttpClient(new ReferenceHandler(this));
            _httpFactory.CreateClient(OgcProcessInputReferenceHttpClient.Name).Returns(_client);
        }

        public async Task<ProcessEndpoints.InputNormalizationResult> NormalizeAsync(string processId, string inputs)
        {
            var request = new OgcExecuteRequest
            {
                Inputs = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputs)!.ToImmutableDictionary()
            };
            await Service.EnsureCallerAuthorizedAsync(Principal, OperatorResourceType.Process, OperatorOperation.Execute);
            return await ProcessEndpoints.NormalizeInputReferencesAsync(request, _catalog.GetProcess(processId)!,
                _httpFactory, 8192, async plan =>
                {
                    var bound = await GeoprocessingRasterSourceResolution.BindLayerIdsAsync(
                        plan, _catalog, RasterResolver, CancellationToken.None);
                    return await Service.EnsurePlanExecutionTierAuthorizedAsync(bound, Principal);
                }, CancellationToken.None);
        }

        public void Dispose()
        {
            _accessor.HttpContext = null;
            _client.Dispose();
            _services.Dispose();
        }

        private sealed class ReferenceHandler(AuthorizationFixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var identifier = request.RequestUri!.AbsolutePath == "/identifier";
                fixture.Requests.Add(request.RequestUri.AbsolutePath);
                if (!identifier)
                {
                    fixture.OnPayload?.Invoke();
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(identifier ? fixture.Identifier
                        : request.RequestUri.AbsolutePath == "/features" ? """{"type":"FeatureCollection","features":[]}"""
                        : "25.5", Encoding.UTF8, "text/plain")
                });
            }
        }
    }
}
