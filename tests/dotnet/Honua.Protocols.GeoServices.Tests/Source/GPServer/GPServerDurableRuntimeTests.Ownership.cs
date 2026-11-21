// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerDurableRuntimeTests
{
    // Every case submits its own job on one host and another caller tries one operation on it.
    // A separate class, so no other case's host runs job workers on the shared job tables while
    // this host is alive.
    [Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
    [Protocol(TestProtocols.GPServer)]
    public sealed class OwnershipTests(OwnershipHost host) : IClassFixture<OwnershipHost>
    {
        private const string OutputNames = "<ParameterNames><String>outputScalar</String></ParameterNames>";

        [IntegrationTheory]
        [InlineData("GetJobStatus")]
        [InlineData("GetJobMessages")]
        [InlineData("GetJobToolName")]
        [InlineData("GetJobResult")]
        [InlineData("CancelJob")]
        [Operation(Operations.ErrorHandling)]
        [Endpoint("POST /services/{serviceId}/GPServer")]
        public async Task SoapJobOperation_OtherCaller_IsDeniedByCanonicalJobOwnership(string operation)
        {
            using var owner = CreateCaller(host.Application, "alice");
            using var other = CreateCaller(host.Application, "bob");
            var jobStore = host.Application.GetService<IExecutionJobStore>();
            var jobId = await SubmitOwnedAreaJobAsync(owner, jobStore);

            using var denied = await PostSoapAsync(other, operation,
                $"<JobID>{jobId}</JobID>" + (operation == "GetJobResult" ? OutputNames : string.Empty));
            var body = await denied.Content.ReadAsStringAsync();
            // The canonical denial is not-found, so another caller cannot confirm that
            // the job exists. A job read without the ownership check would return 200,
            // and a cancel of this already-terminal job would return 412.
            denied.StatusCode.Should().Be(HttpStatusCode.NotFound, body);
            XDocument.Parse(body).Descendants(XName.Get("Fault", "http://schemas.xmlsoap.org/soap/envelope/"))
                .Should().ContainSingle();
            body.Should().NotContain("esriJob").And.NotContain("geometry.area")
                .And.NotContain("Honua_67656F6D657472792E61726561").And.NotContain("data:application/json");

            await AssertOwnerStillReadsAreaAsync(owner, jobStore, jobId);
        }

        private static HttpClient CreateCaller(WebAppFixture fixture, string name)
        {
            var caller = fixture.CreateClient(client =>
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, name);
                client.DefaultRequestHeaders.Add(TestAuthHandler.SubjectHeader, name);
            });
            caller.Timeout = GPServerJobWait.RequestTimeout;
            return caller;
        }

        // Submits the 3 by 4 area job as alice, waits for it and checks it is durably owned by her.
        private static async Task<string> SubmitOwnedAreaJobAsync(HttpClient owner, IExecutionJobStore jobStore)
        {
            var submitted = await SendSoapAsync(owner, "SubmitJob",
                "<ToolName>Honua_67656F6D657472792E61726561</ToolName><Values xsi:type=\"tns:GPValues\">" +
                $"<GPValue xsi:type=\"tns:GPString\"><Value>{PolygonWkb((0, 0), (3, 0), (3, 4), (0, 4), (0, 0))}</Value></GPValue>" +
                "<GPValue xsi:type=\"tns:GPLong\"><Value>3857</Value></GPValue></Values>" +
                GPServerSoapRequestFixtures.ArcPyDefaultControls);
            var jobId = submitted.Value;
            await WaitForSoapJobSucceededAsync(owner, jobId, jobStore);
            var stored = (await jobStore.GetAsync(jobId))!;
            var durableOwner = CanonicalSecurityActor.Resolve(new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "alice"),
                    new Claim(ClaimTypes.NameIdentifier, "alice"),
                    new Claim("sub", "alice")
                ],
                TestAuthHandler.SchemeName)))!.ActorId;
            stored.Audit.RequestedBy.Should().Be(durableOwner);
            stored.Audit.SubmitterSecurityContext!.OwnerActorId.Should().Be(durableOwner);
            return jobId;
        }

        // The owner still reads the unchanged job and its independently computed 3 by 4 area.
        private static async Task AssertOwnerStillReadsAreaAsync(HttpClient owner, IExecutionJobStore jobStore, string jobId)
        {
            (await SendSoapAsync(owner, "GetJobStatus", $"<JobID>{jobId}</JobID>")).Value.Should().Be("esriJobSucceeded");
            (await jobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Succeeded);
            var result = await SendSoapAsync(owner, "GetJobResult", $"<JobID>{jobId}</JobID>{OutputNames}");
            var dataUri = result.Element("Values")!.Elements("GPValue").Should().ContainSingle().Subject.Element("Value")!.Value;
            const string prefix = "data:application/json;base64,";
            dataUri.Should().StartWith(prefix);
            using var measure = JsonDocument.Parse(Convert.FromBase64String(dataUri[prefix.Length..]));
            measure.RootElement.GetProperty("value").GetDouble().Should().Be(3 * 4);
        }
    }

    // Only the operator grant is substituted, so both callers may execute and read jobs;
    // per-job ownership stays with the real job service and job store.
    public sealed class OwnershipHost : IAsyncLifetime
    {
        public WebAppFixture Application { get; } = CreateOwnershipFixture();

        public Task InitializeAsync() => PostgresJobHost.StartAsync(Application);

        public Task DisposeAsync() => Application.DisposeAsync();

        private static WebAppFixture CreateOwnershipFixture()
        {
            var authorizer = Substitute.For<IOperatorAuthorizationEvaluator>();
            authorizer.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
                .Returns(AccessDecision.Allowed());
            var approval = Substitute.For<IOperatorApprovalEvaluator>();
            approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
                .Returns(ApprovalRequirement.NotRequired());
            return CreateDurableFixture().ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOperatorAuthorizationEvaluator>();
                    services.AddSingleton(authorizer);
                    services.RemoveAll<IOperatorApprovalEvaluator>();
                    services.AddSingleton(approval);
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
        }
    }
}
