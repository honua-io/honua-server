// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Host composition for a control-plane event Lambda on a Redis-off cell: the real composition root
/// booted in <c>Production</c> with <c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER</c> set,
/// <c>ControlPlane:TriggerMode=Event</c>, and no Redis. On honua-release run 38242272260
/// (aws-serverless, Redis-off) the cp-backstop and cp-tick functions answered every
/// <c>POST /events</c> with 500 because <see cref="IControlPlaneLambdaEventTargets"/> was registered
/// only with Redis. Owner decision 3: a Redis-off cell's event functions run the Redis-independent
/// work and refuse the rest with a typed 503, never an unhandled 500.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class ControlPlaneLambdaEventRedisOffHostTests(ITestOutputHelper output) : IAsyncLifetime
{
    // Production enforces a complex bootstrap admin password.
    private const string AdminPassword = "Synthetic-RedisOff-Events-S38!";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture
            .UseEnvironment(Environments.Production)
            .ConfigureWebHost(builder =>
            {
                // Deliberately no ConnectionStrings:redis.
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting(ControlPlaneLambdaConnectionRelease.HandlerSelectorKey, "scheduled-tick");
                builder.UseSetting("ControlPlane:TriggerMode", "Event");
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new LoopbackStartupFilter()));
            });
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /events")]
    public void RedisOffEventHost_ResolvesTheEventTargets()
    {
        var targets = _fixture.Services.GetService<IControlPlaneLambdaEventTargets>();

        targets.Should().NotBeNull("the /events route is mapped whenever the handler selector is set");
        ((ControlPlaneLambdaEventTargets)targets!).DurableControlPlaneComposed
            .Should().BeFalse("the host under test has no Redis-backed control plane");
        _fixture.Services.GetService<IScheduledTickDispatcher>().Should().NotBeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /events")]
    public async Task RedisOffEventHost_EveryTickKind_Answers200OrTyped503_Never500()
    {
        using var client = _fixture.CreateClient();
        var answers = new List<(ScheduledTickKind Kind, HttpStatusCode Status, string Body)>();

        foreach (var kind in Enum.GetValues<ScheduledTickKind>())
        {
            using var content = new StringContent($$"""{"kind":"{{kind}}"}""", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri("/events", UriKind.Relative), content);
            answers.Add((kind, response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        var report = string.Join(Environment.NewLine, answers.Select(answer => $"{answer.Kind}: {(int)answer.Status} {answer.Body}"));
        output.WriteLine(report);
        answers.Should().OnlyContain(
            answer => answer.Status == HttpStatusCode.OK || answer.Status == HttpStatusCode.ServiceUnavailable,
            report);

        foreach (var kind in ControlPlaneLambdaEventTargets.DurableStoreTickKinds)
        {
            var answer = answers.Single(candidate => candidate.Kind == kind);
            answer.Status.Should().Be(HttpStatusCode.ServiceUnavailable, report);
            answer.Body.Should().Contain("\"missingDependency\":\"redis\"", report);
        }

        // Temporary-file cleanup is composed on every host and needs no Redis.
        answers.Single(answer => answer.Kind == ScheduledTickKind.TemporaryFileCleanup)
            .Status.Should().Be(HttpStatusCode.OK, report);
    }

    [IntegrationTheory]
    [InlineData("batch-event", """{"source":"aws.batch","detail-type":"Batch Job State Change","detail":{"jobId":"batch-job-s38"}}""")]
    [InlineData("backstop", "{}")]
    public async Task RedisOffEventHost_DurableStoreHandlers_AnswerTyped503(string selector, string payload)
    {
        // The host's own selector is scheduled-tick; the other two handlers run through the same
        // dispatch against the host's real services with their selector.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ControlPlaneLambdaConnectionRelease.HandlerSelectorKey] = selector,
            })
            .Build();
        using var scope = _fixture.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/events";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Response.Body = new MemoryStream();

        var result = await ControlPlaneLambdaEventEndpoint.HandleAsync(
            context,
            configuration,
            scope.ServiceProvider.GetService<IControlPlaneLambdaEventTargets>(),
            NullLogger.Instance,
            new ColdStartWarningLatch());
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable, body);
        body.Should().Contain("\"missingDependency\":\"redis\"")
            .And.Contain($"\"code\":\"{CapabilityUnavailableCodes.ErrorCode}\"");
    }

    /// <summary>
    /// Makes each in-process request look like the Lambda Web Adapter's: loopback at both ends.
    /// </summary>
    private sealed class LoopbackStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    context.Connection.LocalIpAddress = IPAddress.Loopback;
                    await nextMiddleware(context).ConfigureAwait(false);
                });
                next(app);
            };
    }
}
