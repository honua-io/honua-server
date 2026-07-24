// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.StudioAi;

/// <summary>
/// Endpoint-level tests for the Studio AI proxy (honua-server#3000): admin authorization on both
/// routes, the capabilities shape, and — the audit-record requirement (REQ-002) — that a chat call
/// reaching a known, configured (but unreachable) provider still produces exactly one audit record
/// with the expected action/resource/outcome. No live provider is called anywhere in this file: the
/// "provider" here is an unreachable loopback port, so the HTTP call fails fast with a connection
/// error that the adapter turns into a normal <c>error</c> SSE event.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StudioAiProxyEndpointsTests : IAsyncLifetime
{
    private const string ProviderName = "test-openai";
    private readonly CapturingAuditLog _audit = new();
    private readonly WebAppFixture _fixture;

    public StudioAiProxyEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAuditLog>(_audit);
            })
            .ConfigureWebHost(builder =>
            {
                // WebAppFixture's common host settings enable a dev-auth bypass
                // (HONUA_DEV_AUTH=true) that auto-authenticates every request as admin in the
                // "Test" environment — real for the shared-bootstrap fixture, but it would make
                // the anonymous-401 assertions below meaningless. Disable it explicitly and
                // supply our own admin password (mirrors FieldMaskPolicyEndpointsTests), reusing
                // WebAppFixture.SharedAdminPassword so CreateAdminClient() still authenticates.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["StudioAiProxy:Enabled"] = "true",
                        ["StudioAiProxy:DefaultProvider"] = ProviderName,
                        [$"StudioAiProxy:Providers:{ProviderName}:Kind"] = "openai",
                        // Port 1 is a privileged port nothing listens on in the test sandbox, so the
                        // adapter's HTTP call fails fast (connection refused) instead of hanging.
                        [$"StudioAiProxy:Providers:{ProviderName}:Endpoint"] = "http://127.0.0.1:1/v1",
                        [$"StudioAiProxy:Providers:{ProviderName}:Model"] = "test-model",
                        [$"StudioAiProxy:Providers:{ProviderName}:ApiKey"] = "test-key",
                        [$"StudioAiProxy:Providers:{ProviderName}:TimeoutSeconds"] = "5"
                    });
                });
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    public async Task GetCapabilities_Anonymous_Returns401()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/api/v1/studio/ai/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/ai/capabilities")]
    public async Task GetCapabilities_Admin_ReturnsConfiguredProvider()
    {
        var client = _fixture.CreateAdminClient();

        var response = await client.GetAsync("/api/v1/studio/ai/capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("enabled").GetBoolean().Should().BeTrue();
        data.GetProperty("defaultProvider").GetString().Should().Be(ProviderName);

        var providers = data.GetProperty("providers");
        providers.GetArrayLength().Should().Be(1);
        var provider = providers[0];
        provider.GetProperty("provider").GetString().Should().Be(ProviderName);
        provider.GetProperty("kind").GetString().Should().Be("openai");
        provider.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        provider.GetProperty("configured").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_Anonymous_Returns401()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_UnknownProvider_Returns400AndDoesNotAudit()
    {
        var client = _fixture.CreateAdminClient();
        _audit.Recorded.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            provider = "does-not-exist",
            messages = new[] { new { role = "user", content = "hi" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _audit.Recorded.Should().NotContain(e => e.Action == "studio_ai.chat",
            because: "a request that never resolves to a configured provider has no action to attribute");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/studio/ai/chat")]
    public async Task PostChat_KnownButUnreachableProvider_StreamsErrorEventAndRecordsOneFailureAudit()
    {
        var client = _fixture.CreateAdminClient();
        _audit.Recorded.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/studio/ai/chat", new
        {
            messages = new[] { new { role = "user", content = "hi" } }
        });

        // The request named a real, configured provider, so the SSE response headers are already
        // committed by the time the (unreachable) provider call fails — the failure surfaces as an
        // `error` frame in a 200 SSE body, not as an HTTP error status. The connection failure
        // happens before any bytes come back from the provider, so `message_start` (emitted only
        // once a response is actually received) never fires — just the terminal `error` frame.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: error");

        var chatAudits = _audit.Recorded.Where(e => e.Action == "studio_ai.chat").ToList();
        chatAudits.Should().ContainSingle("exactly one audit record must be written per call, success or failure");
        var audit = chatAudits[0];
        audit.EventType.Should().Be(AuditEventType.AdminAction);
        audit.ResourceType.Should().Be("studio_ai_provider");
        audit.ResourceId.Should().Be(ProviderName);
        audit.Outcome.Should().Be(AuditOutcome.Failure);
        audit.Details.Should().Contain("\"kind\":\"openai\"");
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Recorded { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }
}
