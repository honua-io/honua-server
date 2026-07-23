// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Honua.Server.Features.Mobile.FieldCollection.Automations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

public sealed class WebhookFieldCollectionActionHandlerTests
{
    private static FieldCollectionAutomationEvent CreateEvent(string? feature = null)
        => new()
        {
            ClientId = "device-1",
            ChangeId = "chg-1",
            FeatureId = "feat-1",
            LayerId = 42,
            Operation = FieldCollectionChangeOperation.Insert,
            Version = 3,
            Generation = 7,
            Timestamp = DateTimeOffset.UnixEpoch,
            FeaturePayloadJson = feature,
        };

    private static FieldCollectionActionInvocation CreateInvocation(
        string url = "https://example.com/hook",
        string? secret = "signing-secret",
        string? feature = "{\"type\":\"Feature\"}")
    {
        var config = ImmutableDictionary<string, string>.Empty;
        config = config.Add(WebhookFieldCollectionActionHandler.UrlKey, url);
        if (secret is not null)
        {
            config = config.Add(WebhookFieldCollectionActionHandler.SecretKey, secret);
        }

        var action = new FieldCollectionAutomationAction
        {
            Id = "webhook-1",
            DisplayName = "webhook-1",
            ActionType = FieldCollectionAutomationActionType.Webhook,
            Configuration = config,
        };

        return FieldCollectionActionInvocation.Create(action, CreateEvent(feature));
    }

    [UnitTest]
    public async Task ExecuteAsync_PublicDestination_PostsSignedPayloadAndSucceeds()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(WebhookFieldCollectionActionHandler.HttpClientName)
            .Returns(httpClient);

        var sut = new WebhookFieldCollectionActionHandler(httpClientFactory);

        var result = await sut.ExecuteAsync(CreateInvocation());

        Assert.True(result.Succeeded);
        Assert.NotNull(handler.SignatureHeader);
        Assert.StartsWith("sha256=", handler.SignatureHeader);
        Assert.Equal("webhook-1", handler.ActionHeader);
        Assert.Equal("device-1:chg-1:webhook-1", handler.IdempotencyHeader);
        Assert.NotNull(handler.Body);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("chg-1", doc.RootElement.GetProperty("changeId").GetString());
        Assert.Equal("insert", doc.RootElement.GetProperty("operation").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("layerId").GetInt32());
        Assert.Equal("Feature", doc.RootElement.GetProperty("feature").GetProperty("type").GetString());
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingUrl_ReturnsNonRetryableFailureWithoutSending()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var action = new FieldCollectionAutomationAction
        {
            Id = "webhook-1",
            DisplayName = "webhook-1",
            ActionType = FieldCollectionAutomationActionType.Webhook,
            Configuration = ImmutableDictionary<string, string>.Empty,
        };
        var invocation = FieldCollectionActionInvocation.Create(action, CreateEvent());

        var sut = new WebhookFieldCollectionActionHandler(httpClientFactory);
        var result = await sut.ExecuteAsync(invocation);

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingSecret_ReturnsNonRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sut = new WebhookFieldCollectionActionHandler(httpClientFactory);

        var result = await sut.ExecuteAsync(CreateInvocation(secret: null));

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        Assert.Contains("secret", result.Error, StringComparison.OrdinalIgnoreCase);
        httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [UnitTest]
    public async Task ExecuteAsync_UnsafeDestination_ReturnsNonRetryableFailureWithoutSending()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var sut = new WebhookFieldCollectionActionHandler(httpClientFactory);

        var result = await sut.ExecuteAsync(CreateInvocation(url: "https://localhost/hook"));

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
        httpClientFactory.DidNotReceive().CreateClient(Arg.Any<string>());
    }

    [UnitTest]
    public async Task ExecuteAsync_ServerError_ReturnsRetryableFailure()
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var handler = new StatusHandler(HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(handler);
        httpClientFactory.CreateClient(WebhookFieldCollectionActionHandler.HttpClientName)
            .Returns(httpClient);

        var sut = new WebhookFieldCollectionActionHandler(httpClientFactory);
        var result = await sut.ExecuteAsync(CreateInvocation());

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [UnitTest]
    public void BuildPayload_DeleteOperation_WritesNullFeature()
    {
        var payload = WebhookFieldCollectionActionHandler.BuildPayload(new FieldCollectionAutomationEvent
        {
            ClientId = "device-1",
            ChangeId = "chg-9",
            FeatureId = "feat-9",
            LayerId = 1,
            Operation = FieldCollectionChangeOperation.Delete,
            Version = 5,
            Generation = 5,
            Timestamp = DateTimeOffset.UnixEpoch,
            FeaturePayloadJson = null,
        });

        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("feature").ValueKind);
        Assert.Equal("delete", doc.RootElement.GetProperty("operation").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? SignatureHeader { get; private set; }

        public string? ActionHeader { get; private set; }

        public string? IdempotencyHeader { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SignatureHeader = request.Headers.GetValues("X-Honua-Signature").Single();
            ActionHeader = request.Headers.GetValues("X-Honua-FieldCollection-Action").Single();
            IdempotencyHeader = request.Headers.GetValues("Idempotency-Key").Single();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusHandler(HttpStatusCode status) => _status = status;

        // Ownership of the HttpResponseMessage transfers to the caller (HttpClient/handler chain);
        // it cannot be disposed here since it must be returned intact.
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            // codeql[cs/local-not-disposed] -- ownership transfers to the returned or containing disposable object.
            => Task.FromResult(new HttpResponseMessage(_status));
    }
}
