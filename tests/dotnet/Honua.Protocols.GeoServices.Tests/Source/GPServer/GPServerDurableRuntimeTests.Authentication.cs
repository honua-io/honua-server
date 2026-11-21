// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerDurableRuntimeTests
{
    [Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
    [Protocol(TestProtocols.GPServer)]
    public sealed class AuthenticationTests(AuthenticationHost host) : IClassFixture<AuthenticationHost>
    {
        [IntegrationTheory]
        [InlineData("anonymous")]
        [InlineData("invalid-api-key")]
        [InlineData("invalid-bearer")]
        [Operation(Operations.ErrorHandling)]
        [Endpoint("POST /services/{serviceId}/GPServer")]
        public async Task SoapJobOperation_UnauthenticatedCaller_IsChallengedWithoutJobState(string caller)
        {
            var fixture = host.Application;
            using var owner = fixture.CreateAdminClient();
            using var denied = fixture.CreateClient(client =>
            {
                if (caller == "invalid-api-key")
                {
                    client.DefaultRequestHeaders.Add("X-API-Key", "honua-test-" + Guid.NewGuid().ToString("N"));
                }
                else if (caller == "invalid-bearer")
                {
                    client.DefaultRequestHeaders.Add("Authorization", "Bearer honua-test-" + Guid.NewGuid().ToString("N"));
                }
            });
            owner.Timeout = GPServerJobWait.RequestTimeout;
            denied.Timeout = GPServerJobWait.RequestTimeout;
            var area = "<ToolName>Honua_67656F6D657472792E61726561</ToolName><Values xsi:type=\"tns:GPValues\">" +
                $"<GPValue xsi:type=\"tns:GPString\"><Value>{PolygonWkb((0, 0), (3, 0), (3, 4), (0, 4), (0, 0))}</Value></GPValue>" +
                "<GPValue xsi:type=\"tns:GPLong\"><Value>3857</Value></GPValue></Values>" +
                GPServerSoapRequestFixtures.ArcPyDefaultControls;
            var jobId = (await SendSoapAsync(owner, "SubmitJob", area)).Value;
            var jobStore = fixture.GetService<IExecutionJobStore>();
            await WaitForSoapJobSucceededAsync(owner, jobId, jobStore);
            var jobsBefore = (await jobStore.QueryAsync(new ExecutionJobQuery())).Items.Count;

            await AssertUnauthenticatedSoapOperationsAsync(denied, jobId, area);

            // No challenged submission created a job, and the refused cancel left the
            // owner's job and its independently computed 3 by 4 area unchanged.
            (await jobStore.QueryAsync(new ExecutionJobQuery())).Items.Count.Should().Be(jobsBefore);
            (await jobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            const string outputNames = "<ParameterNames><String>outputScalar</String></ParameterNames>";
            var result = await SendSoapAsync(owner, "GetJobResult", $"<JobID>{jobId}</JobID>{outputNames}");
            var dataUri = result.Element("Values")!.Elements("GPValue").Should().ContainSingle().Subject.Element("Value")!.Value;
            const string prefix = "data:application/json;base64,";
            dataUri.Should().StartWith(prefix);
            using var measure = JsonDocument.Parse(Convert.FromBase64String(dataUri[prefix.Length..]));
            measure.RootElement.GetProperty("value").GetDouble().Should().Be(3 * 4);
        }

        private static async Task AssertUnauthenticatedSoapOperationsAsync(HttpClient denied, string jobId, string area)
        {
            var outputNames = "<ParameterNames><String>outputScalar</String></ParameterNames>";
            foreach (var (operation, arguments) in new[]
            {
                ("SubmitJob", area),
                ("Execute", area),
                ("GetJobStatus", $"<JobID>{jobId}</JobID>"),
                ("GetJobMessages", $"<JobID>{jobId}</JobID>"),
                ("GetJobToolName", $"<JobID>{jobId}</JobID>"),
                ("GetJobResult", $"<JobID>{jobId}</JobID>{outputNames}"),
                ("CancelJob", $"<JobID>{jobId}</JobID>"),
            })
            {
                using var response = await PostSoapAsync(denied, operation, arguments);
                var body = await response.Content.ReadAsStringAsync();
                // Authentication is decided before submission parsing or any job lookup,
                // so the challenge cannot confirm the job, its task or its result.
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{operation}: {body}");
                XDocument.Parse(body).Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/"))
                    .Should().ContainSingle(operation);
                body.Should().NotContain("esriJob").And.NotContain("geometry.area").And.NotContain(jobId)
                    .And.NotContain("Honua_67656F6D657472792E61726561").And.NotContain("data:application/json");
            }
        }
    }

    public sealed class AuthenticationHost : IAsyncLifetime
    {
        public WebAppFixture Application { get; } = CreateDurableFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });

        public Task InitializeAsync() => PostgresJobHost.StartAsync(Application);

        public Task DisposeAsync() => Application.DisposeAsync();
    }
}
