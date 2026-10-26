// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Security;
using Honua.Server.Features.Reporting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>Exercises the optional narrative provider's outbound privacy boundary.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class OpenAiNarrativeProviderTests
{
    [UnitTheory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [Operation(Operations.Metadata)]
    public async Task GenerateAsync_WhenProviderRejectsRequest_DoesNotExposeResponseBody(HttpStatusCode status)
    {
        const string canary = "FAKE_PROMPT_AND_PROVIDER_KEY_CANARY";
        using var handler = new ResponseHandler(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(canary)
        });
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(client, cache);

        var act = () => provider.GenerateAsync(CreateDraft(), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
        exception.Which.ToString().Should().NotContain(canary);
        handler.RequestCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GenerateAsync_WhenProviderSucceeds_ReturnsAndCachesNarrative()
    {
        using var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"{\"slots\":{\"summary\":\"Approved narrative\"}}"}}]}""")
        });
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(client, cache);

        var first = await provider.GenerateAsync(CreateDraft(), CancellationToken.None);
        var second = await provider.GenerateAsync(CreateDraft(), CancellationToken.None);

        first.SlotText["summary"].Should().Be("Approved narrative");
        second.Should().BeSameAs(first);
        handler.RequestCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GenerateAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var handler = new ResponseHandler(() => throw new OperationCanceledException());
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(client, cache);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => provider.GenerateAsync(CreateDraft(), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    public async Task AddAnalysisReporting_WhenEndpointRedirects_SendsNothingToRedirectTarget()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectedRequests = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var line = await reader.ReadLineAsync(cancellation.Token);
                    var isTarget = line?.Contains("/target", StringComparison.Ordinal) == true;
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellation.Token))) { }
                    if (isTarget)
                    {
                        Interlocked.Increment(ref redirectedRequests);
                    }
                    var response = isTarget
                        ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/target\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Listener cancellation is the expected fixture shutdown path.
                return;
            }
        }, cancellation.Token);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reporting:Narrative:Enabled"] = "true"
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAnalysisReporting(configuration);
            await using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("reporting-narrative");

            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/source"), cancellation.Token);

            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
            Volatile.Read(ref redirectedRequests).Should().Be(0);
        }
        finally
        {
            await cancellation.CancelAsync();
            await server;
        }
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GenerateAsync_UnapprovedDisclosure_ReturnsEmptyWithoutProviderCall()
    {
        using var handler = new ResponseHandler(() => throw new InvalidOperationException("must not call"));
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = CreateProvider(client, cache, new AiDisclosurePolicyOptions());
        var fill = await provider.GenerateAsync(CreateDraft(), CancellationToken.None);
        fill.SlotText.Should().BeEmpty();
        handler.RequestCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public async Task GenerateAsync_ApprovalRemovedAfterCachedResult_DoesNotReturnCachedModelText()
    {
        using var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"{\"slots\":{\"summary\":\"Approved narrative\"}}"}}]}""")
        });
        using var client = new HttpClient(handler);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var policy = ApprovedDisclosure();
        var provider = CreateProvider(client, cache, policy);
        (await provider.GenerateAsync(CreateDraft(), CancellationToken.None)).SlotText.Should().NotBeEmpty();
        policy.Approvals.Clear();
        (await provider.GenerateAsync(CreateDraft(), CancellationToken.None)).SlotText.Should().BeEmpty();
        handler.RequestCount.Should().Be(1);
    }

    private static AiDisclosurePolicyOptions ApprovedDisclosure() => new()
    {
        Approvals = [new() { Provider = "openai", Kind = "openai", Destination = "https://api.openai.com/v1", Model = "gpt-4o-mini", UseCase = "reporting-narrative", DataClassification = "unclassified" }]
    };

    private static OpenAiNarrativeProvider CreateProvider(HttpClient client, IMemoryCache cache, AiDisclosurePolicyOptions? disclosure = null)
        => new(new ClientFactory(client), cache, Options.Create(new ReportingConfiguration
        {
            Narrative = new ReportingNarrativeConfiguration
            {
                Enabled = true,
                ApiKey = "fake-api-key",
                Disclosure = disclosure ?? ApprovedDisclosure()
            }
        }), NullLogger<OpenAiNarrativeProvider>.Instance);

    private static AnalysisReportDraft CreateDraft() => new()
    {
        TemplateId = "privacy-test",
        TemplateVersion = "1.0.0",
        ProcessId = "analytics.buffer-aggregate",
        ProcessFamily = "analytics",
        Sections = [],
        NarrativeSlots = [new NarrativeSlot { SlotId = "summary", DeterministicText = "Deterministic summary" }],
        SourcePackage = ReportingFixtures.BufferAggregatePackage()
    };

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response());
        }
    }
}
