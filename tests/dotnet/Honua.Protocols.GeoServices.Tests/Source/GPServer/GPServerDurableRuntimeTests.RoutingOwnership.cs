// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerDurableRuntimeTests
{
    private WebAppFixture CreateRoutingAuthFixture()
        => CreateDurableFixture(productionExecutor: true)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Routing:Provider", "mock");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                    });
                });
            });

    // A display name is not a durable owner: without a subject or API-key id the shared
    // job service refuses the submission before any job record or approval proposal exists.
    // Both routing tasks share one host to keep the shard within its capacity budget.
    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task RoutingJobs_OwnerlessSubmission_AreRefusedWithoutJob()
    {
        await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        var fixture = CreateRoutingAuthFixture();
        await fixture.InitializeAsync();
        try
        {
            using var ownerless = fixture.CreateClient(client =>
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "routing-display-name");
                client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin");
                client.Timeout = GPServerJobWait.RequestTimeout;
            });
            var store = fixture.GetService<IExecutionJobStore>();
            foreach (var task in new[] { "FindRoutes", "GenerateServiceAreas" })
            {
                var idempotencyKey = $"ownerless-{task}";
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    [task == "FindRoutes" ? "Stops" : "Facilities"] = "-157.858333,21.306944;-157.862,21.31",
                    [task == "FindRoutes" ? "Measurement_Units" : "Break_Units"] = "Minutes",
                    ["idempotencyKey"] = idempotencyKey,
                });
                using var response = await ownerless.PostAsync($"/rest/services/{ServiceId}/GPServer/{task}/submitJob", content);
                var body = await response.Content.ReadAsStringAsync();
                using var refused = JsonDocument.Parse(body);
                var error = refused.RootElement.GetProperty("error");
                error.GetProperty("code").GetInt32().Should().Be(400, body);
                error.GetProperty("details").EnumerateArray().Select(detail => detail.GetString())
                    .Should().Contain("A durable submitter identity is required for job submissions.", body);
                refused.RootElement.TryGetProperty("jobId", out _).Should().BeFalse(body);
                refused.RootElement.TryGetProperty("results", out _).Should().BeFalse(body);
                (await store.GetAsync(GeoprocessingJobService.CreateJobId(idempotencyKey))).Should().BeNull(task);
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        }
    }

    [IntegrationTheory]
    [InlineData("FindRoutes", "Output_Routes")]
    [InlineData("GenerateServiceAreas", "Service_Areas")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task RoutingJob_TenantBoundDurableOwner_DeniesOtherTenantEvenForAdministrator(string task, string output)
    {
        await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        var fixture = CreateRoutingAuthFixture();
        await fixture.InitializeAsync();
        try
        {
            HttpClient CreateTenantClient(string tenant) => fixture.CreateClient(client =>
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "routing-admin");
                client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, "routing-subject");
                client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, "admin,multi_tenant_admin");
                client.DefaultRequestHeaders.Add(TenantContextOptions.TenantHeaderName, tenant);
                client.Timeout = GPServerJobWait.RequestTimeout;
            });
            using var owner = CreateTenantClient("routing-tenant-a");
            using var otherTenant = CreateTenantClient("routing-tenant-b");
            var taskUrl = $"/rest/services/{ServiceId}/GPServer/{task}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                [task == "FindRoutes" ? "Stops" : "Facilities"] = "-157.858333,21.306944;-157.862,21.31",
                [task == "FindRoutes" ? "Measurement_Units" : "Break_Units"] = "Minutes",
                ["idempotencyKey"] = "shared-routing-key",
            });
            using var submit = await owner.PostAsync(taskUrl + "/submitJob", content);
            var body = await submit.Content.ReadAsStringAsync();
            submit.StatusCode.Should().Be(HttpStatusCode.OK, body);
            using var submitted = JsonDocument.Parse(body);
            submitted.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
            var jobId = submitted.RootElement.GetProperty("jobId").GetString()!;
            var store = fixture.GetService<IExecutionJobStore>();
            using var terminal = await GPServerJobWait.UntilRestSucceededAsync(owner,
                $"{taskUrl}/jobs/{jobId}?f=json", jobId, store);
            var job = (await store.GetAsync(jobId))!;
            var actor = CanonicalSecurityActor.Resolve(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "routing-subject"), new Claim("sub", "routing-subject")],
                TestAuthHandler.SchemeName)))!.ActorId;
            job.Audit.RequestedBy.Should().Be(actor);
            job.Audit.SubmitterSecurityContext!.OwnerActorId.Should().Be(actor);
            job.Audit.SubmitterSecurityContext.TenantId.Should().Be("routing-tenant-a");

            static async Task AssertRestJobNotFoundAsync(HttpResponseMessage response)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, errorBody);
                using var error = JsonDocument.Parse(errorBody);
                error.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
                error.RootElement.TryGetProperty("jobId", out _).Should().BeFalse(errorBody);
                error.RootElement.TryGetProperty("results", out _).Should().BeFalse(errorBody);
                error.RootElement.TryGetProperty("value", out _).Should().BeFalse(errorBody);
            }

            foreach (var suffix in new[] { "?f=json", $"/results/{output}?f=json" })
            {
                using var denied = await otherTenant.GetAsync($"{taskUrl}/jobs/{jobId}{suffix}");
                await AssertRestJobNotFoundAsync(denied);
            }
            using var deniedCancel = await otherTenant.PostAsync($"{taskUrl}/jobs/{jobId}/cancel?f=json", null);
            await AssertRestJobNotFoundAsync(deniedCancel);
            using var deniedSoap = await PostSoapAsync(otherTenant, "GetJobStatus", $"<JobID>{jobId}</JobID>");
            deniedSoap.StatusCode.Should().Be(HttpStatusCode.NotFound, await deniedSoap.Content.ReadAsStringAsync());
            XDocument.Parse(await deniedSoap.Content.ReadAsStringAsync())
                .Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/")).Should().ContainSingle();
            (await store.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);

            using var ownerResult = await owner.GetAsync($"{taskUrl}/jobs/{jobId}/results/{output}?f=json");
            ownerResult.StatusCode.Should().Be(HttpStatusCode.OK, await ownerResult.Content.ReadAsStringAsync());
            using var ownerOutput = JsonDocument.Parse(await ownerResult.Content.ReadAsStringAsync());
            ownerOutput.RootElement.TryGetProperty("error", out _).Should().BeFalse(ownerOutput.RootElement.GetRawText());
            ownerOutput.RootElement.GetProperty("value").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
            using var otherContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                [task == "FindRoutes" ? "Stops" : "Facilities"] = "-157.858333,21.306944;-157.862,21.31",
                [task == "FindRoutes" ? "Measurement_Units" : "Break_Units"] = "Minutes",
                ["idempotencyKey"] = "shared-routing-key",
            });
            using var otherSubmit = await otherTenant.PostAsync(taskUrl + "/submitJob", otherContent);
            otherSubmit.StatusCode.Should().Be(HttpStatusCode.OK, await otherSubmit.Content.ReadAsStringAsync());
            using var otherSubmitted = JsonDocument.Parse(await otherSubmit.Content.ReadAsStringAsync());
            otherSubmitted.RootElement.TryGetProperty("error", out _).Should().BeFalse(otherSubmitted.RootElement.GetRawText());
            var otherId = otherSubmitted.RootElement.GetProperty("jobId").GetString()!;
            otherId.Should().NotBe(jobId, "idempotency is scoped by tenant and durable owner");
            using var otherTerminal = await GPServerJobWait.UntilRestSucceededAsync(otherTenant,
                $"{taskUrl}/jobs/{otherId}?f=json", otherId, store);
            (await store.GetAsync(otherId))!.Audit.SubmitterSecurityContext!.TenantId.Should().Be("routing-tenant-b");
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        }
    }
}
