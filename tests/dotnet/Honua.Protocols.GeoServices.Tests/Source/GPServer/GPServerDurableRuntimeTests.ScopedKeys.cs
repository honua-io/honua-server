// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerDurableRuntimeTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapScopedAPIKey_ExactAreaGrant_ExecutesAndOwnsDurableJob()
    {
        await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        var fixture = CreateScopedKeyFixture();
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            using var owner = await CreateScopedGPKeyClientAsync(fixture, admin, $"execute:{ServiceId}/geometry.area");
            using var other = await CreateScopedGPKeyClientAsync(fixture, admin, $"execute:{ServiceId}/geometry.area");
            owner.Timeout = GPServerJobWait.RequestTimeout;
            const string areaTool = "Honua_67656F6D657472792E61726561";
            var arguments = $"<ToolName>{areaTool}</ToolName><Values xsi:type=\"tns:GPValues\">" +
                $"<GPValue xsi:type=\"tns:GPString\"><Value>{PolygonWkb((0, 0), (3, 0), (3, 4), (0, 4), (0, 0))}</Value></GPValue>" +
                "<GPValue xsi:type=\"tns:GPLong\"><Value>3857</Value></GPValue></Values>" +
                GPServerSoapRequestFixtures.ArcPyDefaultControls;
            var jobId = (await SendSoapAsync(owner, "SubmitJob", arguments)).Value;
            var store = fixture.GetService<IExecutionJobStore>();
            await WaitForSoapJobSucceededAsync(owner, jobId, store);
            var job = (await store.GetAsync(jobId))!;
            job.Spec.GPServerBinding.Should().Be(new GPServerJobBinding(ServiceId, areaTool, "geometry.area"));
            (await SendSoapAsync(owner, "GetJobStatus", $"<JobID>{jobId}</JobID>")).Value.Should().Be("esriJobSucceeded");
            foreach (var operation in new[] { "GetJobStatus", "GetJobMessages", "GetJobToolName", "GetJobResult", "CancelJob" })
            {
                using var refused = await PostSoapAsync(other, operation, $"<JobID>{jobId}</JobID>" +
                    (operation == "GetJobResult" ? "<ParameterNames><String>outputScalar</String></ParameterNames>" : string.Empty));
                var refusedBody = await refused.Content.ReadAsStringAsync();
                refused.StatusCode.Should().Be(HttpStatusCode.NotFound, refusedBody);
                refusedBody.Should().NotContain("esriJob").And.NotContain("data:application/json");
            }
            var results = await SendSoapAsync(owner, "GetJobResult", $"<JobID>{jobId}</JobID>" +
                "<ParameterNames><String>outputScalar</String></ParameterNames>");
            var dataUri = results.Element("Values")!.Elements("GPValue").Single().Element("Value")!.Value;
            const string prefix = "data:application/json;base64,";
            dataUri.Should().StartWith(prefix);
            using var result = JsonDocument.Parse(Convert.FromBase64String(dataUri[prefix.Length..]));
            result.RootElement.GetProperty("value").GetDouble().Should().Be(12);
            // The same key has no OGC/operator execute grant, even for the granted canonical process.
            using var generic = await owner.PostAsJsonAsync("/ogc/processes/processes/geometry.area/execution", new { inputs = new { } });
            generic.StatusCode.Should().Be(HttpStatusCode.Forbidden, await generic.Content.ReadAsStringAsync());
            // Synchronous Execute uses the same shared submission recheck and terminal seam.
            var executed = await SendSoapAsync(owner, "Execute", arguments);
            executed.Element("Values").Should().NotBeNull();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData("read-only")]
    [InlineData("write-only")]
    [InlineData("wrong-service")]
    [InlineData("wrong-process")]
    [InlineData("alias-grant")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task SoapScopedAPIKey_UnauthorizedAreaGrant_DeniesBeforeParameterParsing(string grantKind)
    {
        var fixture = CreateScopedKeyFixture();
        await fixture.InitializeAsync();
        try
        {
            using var admin = fixture.CreateAdminClient();
            var permission = grantKind switch
            {
                "read-only" => $"read:{ServiceId}",
                "write-only" => $"write:{ServiceId}",
                "wrong-service" => "execute:other/geometry.area",
                "wrong-process" => $"execute:{ServiceId}/geometry.buffer",
                _ => $"execute:{ServiceId}/Honua_67656F6D657472792E61726561",
            };
            using var denied = await CreateScopedGPKeyClientAsync(fixture, admin, permission);
            using var response = await PostSoapAsync(denied, "SubmitJob",
                "<ToolName>Honua_67656F6D657472792E61726561</ToolName><Malformed/>");
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, text);
            text.Should().NotContain("esriJob");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private WebAppFixture CreateScopedKeyFixture()
        => CreateDurableFixture(productionExecutor: true).ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    private static async Task<HttpClient> CreateScopedGPKeyClientAsync(
        WebAppFixture fixture, HttpClient admin, string executePermission)
    {
        using var response = await admin.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name = "gp-scope-test-" + Guid.NewGuid().ToString("N"),
            permissions = new[] { $"read:{ServiceId}", executePermission }
        });
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        using var document = JsonDocument.Parse(text);
        var key = document.RootElement.GetProperty("data").GetProperty("key").GetString()!;
        return fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
    }
}
