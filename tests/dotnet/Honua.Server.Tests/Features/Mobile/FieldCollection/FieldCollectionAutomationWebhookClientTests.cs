// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Server.Features.Mobile.FieldCollection.Automations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Mobile.FieldCollection;

/// <summary>
/// The FieldCollection automation webhook client must not follow redirects and
/// must pin DNS at connect time (#6126).
/// </summary>
public sealed class FieldCollectionAutomationWebhookClientTests
{
    [UnitTest]
    public void AutomationWebhookClient_DisablesRedirectsAndPinsConnectAddress()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFieldCollectionAutomations(configuration);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(WebhookFieldCollectionActionHandler.HttpClientName);

        var sockets = Assert.IsType<SocketsHttpHandler>(UnwrapPrimaryHandler(client));
        Assert.False(sockets.AllowAutoRedirect);
        Assert.NotNull(sockets.ConnectCallback);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
    }

    private static HttpMessageHandler UnwrapPrimaryHandler(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField(
            "_handler",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var handler = Assert.IsAssignableFrom<HttpMessageHandler>(field.GetValue(client));
        while (handler is DelegatingHandler delegating)
        {
            Assert.NotNull(delegating.InnerHandler);
            handler = delegating.InnerHandler;
        }

        return handler;
    }
}
