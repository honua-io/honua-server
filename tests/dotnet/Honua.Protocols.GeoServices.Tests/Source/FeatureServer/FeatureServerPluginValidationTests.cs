// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Integration tests for the plugin/extension SDK (#347) wired into the FeatureServer applyEdits
/// path. Registers a custom <see cref="IFeatureValidator"/> through the real <c>AddHonuaPlugins</c>
/// pipeline under an Enterprise license and asserts that plugin rejections (1) fail the offending
/// feature, (2) keep it out of the write set, and (3) honour rollback. The rule keys on the
/// layer's <c>name</c> field so the test is independent of layer schema specifics.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
[Collection("Database")]
public sealed class FeatureServerPluginValidationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const string TestServiceId = "test";
    private const int TestLayerId = 0;
    private const string RejectedName = "REJECT";
    private ServiceProvider? _pluginServices;

    public async Task InitializeAsync()
    {
        var featureStore = new TestFeatureStore();
        _fixture.ReplaceService<IFeatureReader>(featureStore);
        _fixture.ReplaceService<IFeatureWriter>(featureStore);
        _fixture.ReplaceService<ITileProvider>(featureStore);
        _fixture.ReplaceService<IRelationshipStore>(featureStore);
        _fixture.ReplaceService<IStreamingFeatureStore>(featureStore);

        // Build the real plugin edit pipeline (Enterprise + a custom validator) through the public
        // AddHonuaPlugins API and inject it, so the FeatureServer edit path exercises genuine
        // plugin validation rather than the default no-op.
        _pluginServices = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise))
            .AddSingleton<IAuditLog, NullAuditLog>()
            .AddHonuaPlugins(new ConfigurationBuilder().Build(), p => p.Add<RejectByNamePlugin>())
            .BuildServiceProvider();
        _fixture.ReplaceService(_pluginServices.GetRequiredService<IPluginEditPipeline>());

        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        if (_pluginServices is not null)
        {
            await _pluginServices.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_FeatureRejectedByPlugin_IsNotWritten()
    {
        var before = await CountFeaturesAsync();

        var response = await PostApplyEditsAsync(new ApplyEditsRequest
        {
            Adds = [Point(RejectedName)]
        });

        response.Be200Ok();
        var result = await ReadApplyEditsAsync(response);

        result.Success.Should().BeFalse();
        result.AddResults.Should().ContainSingle();
        result.AddResults![0].Success.Should().BeFalse();
        result.AddResults[0].Error!.Description.Should().Contain("not permitted");

        var after = await CountFeaturesAsync();
        after.Should().Be(before, "a plugin-rejected feature must not be written");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_FeatureAllowedByPlugin_Succeeds()
    {
        var before = await CountFeaturesAsync();

        var response = await PostApplyEditsAsync(new ApplyEditsRequest
        {
            Adds = [Point("Acceptable Feature")]
        });

        response.Be200Ok();
        var result = await ReadApplyEditsAsync(response);

        result.AddResults.Should().ContainSingle();
        result.AddResults![0].Success.Should().BeTrue();

        var after = await CountFeaturesAsync();
        after.Should().Be(before + 1, "an allowed feature is written");
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_RollbackOnFailure_PluginRejection_RollsBackEntireBatch()
    {
        var before = await CountFeaturesAsync();

        var response = await PostApplyEditsAsync(new ApplyEditsRequest
        {
            RollbackOnFailure = true,
            Adds =
            [
                Point(RejectedName),       // rejected by the plugin
                Point("Otherwise Valid")   // would succeed on its own
            ]
        });

        response.Be200Ok();
        var result = await ReadApplyEditsAsync(response);

        result.Success.Should().BeFalse();
        result.AddResults.Should().HaveCount(2);
        result.AddResults!.Should().OnlyContain(r => !r.Success, "rollbackOnFailure fails the whole batch");

        var after = await CountFeaturesAsync();
        after.Should().Be(before, "rollback must not persist the otherwise-valid feature");
    }

    private static GeoServicesFeature Point(string name)
        => new()
        {
            Attributes = new Dictionary<string, object?> { ["name"] = name },
            Geometry = new GeoServicesGeometry { X = -122.4194, Y = 37.7749 }
        };

    private Task<HttpResponseMessage> PostApplyEditsAsync(ApplyEditsRequest request)
    {
        var json = JsonSerializer.Serialize(request, FeatureServerJsonContext.Default.ApplyEditsRequest);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return _fixture.Client.PostAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/applyEdits", content);
    }

    private static async Task<ApplyEditsResponse> ReadApplyEditsAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<ApplyEditsResponse>(
            body, FeatureServerJsonContext.Default.ApplyEditsResponse);
        parsed.Should().NotBeNull();
        return parsed!;
    }

    private async Task<long> CountFeaturesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestServiceId}/FeatureServer/{TestLayerId}/query?where=1%3D1&returnCountOnly=true&f=json");
        response.Be200Ok();
        var body = await response.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<QueryResponse>(body, FeatureServerJsonContext.Default.QueryResponse);
        return parsed!.Count ?? 0;
    }

    /// <summary>Test plugin that rejects features whose <c>name</c> attribute equals "REJECT".</summary>
    [Plugin("test-reject-by-name", "1.0.0")]
    private sealed class RejectByNamePlugin : IFeatureValidator
    {
        public ValueTask<PluginValidationResult> ValidateAsync(
            Feature feature, PluginValidationContext context, CancellationToken cancellationToken)
        {
            foreach (var (key, value) in feature.Attributes)
            {
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)
                    && value is string text
                    && string.Equals(text, RejectedName, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(PluginValidationResult.Error("name 'REJECT' is not permitted"));
                }
            }

            return ValueTask.FromResult(PluginValidationResult.Success());
        }
    }
}
