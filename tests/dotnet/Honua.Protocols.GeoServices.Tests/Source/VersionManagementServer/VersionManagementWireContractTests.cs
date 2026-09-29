// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.VersionManagementServer;
using Honua.Protocols.GeoServices.VersionManagementServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VersionManagementServer;

public sealed class VersionManagementWireContractTests
{
    [UnitTest]
    public void ServiceInfo_SerializesDocumentedBooleanCapabilityObject()
    {
        // Independent wire keys from Esri's Version Management Service JSON syntax.
        // https://developers.arcgis.com/rest/services-reference/enterprise/version-management-service/
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new VersionManagementServiceInfo(),
            VersionManagementJsonContext.Default.VersionManagementServiceInfo));
        var flags = document.RootElement.GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Object, flags.ValueKind);
        var enabled = new HashSet<string>(StringComparer.Ordinal)
        {
            "supportsConflictDetectionByAttribute", "supportsAsyncReconcile", "supportsAsyncPost",
        };
        string[] keys =
        [
            "supportsConflictDetectionByAttribute", "supportsPartialPost", "supportsDifferencesFromMoment",
            "supportsDifferencesWithLayers", "supportsAsyncReconcile", "supportsAsyncPost", "supportsAsyncDifferences",
            "supportsOutSR", "supportsPartialPostOperation", "supportsVersionInfosNameFilter",
            "supportsMultipleReadersSingleWriterLocking", "supportsLockInfos", "supportsCreateWithMoment",
        ];
        Assert.Equal(keys.Order(), flags.EnumerateObject().Select(property => property.Name).Order());
        foreach (var name in keys)
        {
            Assert.Equal(enabled.Contains(name), flags.GetProperty(name).GetBoolean());
        }
    }

    [Theory]
    [InlineData(VersionJobStatus.Pending, "Pending")]
    [InlineData(VersionJobStatus.Running, "InProgress")]
    [InlineData(VersionJobStatus.Succeeded, "Completed")]
    [InlineData(VersionJobStatus.Failed, "Failed")]
    [InlineData(VersionJobStatus.LockContended, "Failed")]
    public void JobStatus_UsesDocumentedLifecycleAndOutcome(VersionJobStatus state, string expectedStatus)
    {
        var created = DateTimeOffset.FromUnixTimeMilliseconds(1700000000123);
        var terminal = state is not (VersionJobStatus.Pending or VersionJobStatus.Running);
        var job = new VersionJob(Guid.NewGuid(), "svc", Guid.NewGuid(), VersionJobKind.Reconcile,
            state, VersionReconcilePolicy.None, created,
            StartedAt: state == VersionJobStatus.Pending ? null : created.AddSeconds(1),
            CompletedAt: terminal ? created.AddSeconds(2) : null,
            Posted: state == VersionJobStatus.Succeeded);
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/arcgis";
        var response = VersionManagementServerEndpoints.ToJobResponse(context, "svc", job.VersionId.ToString(), job);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response,
            VersionManagementJsonContext.Default.VersionJobResponse));
        var wire = document.RootElement;

        Assert.Equal(expectedStatus, wire.GetProperty("status").GetString());
        Assert.StartsWith("/arcgis/rest/services/svc/VersionManagementServer/versions/", wire.GetProperty("statusUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal(1700000000123, wire.GetProperty("submissionTime").GetInt64());
        Assert.Equal(terminal ? 1700000002123 : state == VersionJobStatus.Pending ? 1700000000123 : 1700000001123,
            wire.GetProperty("lastUpdatedTime").GetInt64());
        Assert.Equal(terminal, wire.TryGetProperty("success", out var success));
        Assert.Equal(terminal, wire.TryGetProperty("moment", out var moment));
        if (terminal)
        {
            Assert.Equal(state == VersionJobStatus.Succeeded, success.GetBoolean());
            Assert.Equal(1700000002123, moment.GetInt64());
        }
        Assert.Equal(expectedStatus == "Failed", wire.TryGetProperty("error", out var error));
        if (expectedStatus == "Failed")
        {
            Assert.Equal(JsonValueKind.Object, error.ValueKind);
            Assert.True(error.GetProperty("extendedCode").GetInt32() != 0);
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
            Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
        }
    }

    [Theory]
    [InlineData(false, "Completed", true)]
    [InlineData(true, "Failed", false)]
    public void JobStatus_LegacyPostRecord_PreservesActualOutcome(bool blocked, string status, bool didPost)
    {
        var job = new VersionJob(Guid.NewGuid(), "svc", Guid.NewGuid(), VersionJobKind.Post,
            VersionJobStatus.Succeeded, VersionReconcilePolicy.None, DateTimeOffset.UtcNow,
            BlockedByConflicts: blocked);
        var response = VersionManagementServerEndpoints.ToJobResponse(new DefaultHttpContext(), "svc", job.VersionId.ToString(), job);

        Assert.Equal(status, response.Status);
        Assert.Equal(didPost, response.Success);
        Assert.Equal(didPost, response.DidPost);
        Assert.Equal(blocked, response.HasConflicts);
    }
}
