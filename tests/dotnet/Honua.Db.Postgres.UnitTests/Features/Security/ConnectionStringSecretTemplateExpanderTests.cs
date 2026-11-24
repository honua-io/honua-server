// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

[Collection("Security")]
[Trait("Tier", "Fast")]
public sealed class ConnectionStringSecretTemplateExpanderTests
{
    [SecurityTest]
    [Fact]
    public async Task ExpandAsync_SecretContainingPlaceholderSyntax_IsNeverReparsed()
    {
        var calls = new List<string>();
        var result = await ConnectionStringSecretTemplateExpander.ExpandAsync(
            "Host=db;Password=${test:primary};Backup={test:secondary}",
            "test:",
            static key => key.StartsWith("test:", StringComparison.OrdinalIgnoreCase),
            (key, _) =>
            {
                calls.Add(key);
                return Task.FromResult<string?>(key switch
                {
                    "test:primary" => "p@ss{test:injected}${test:other}",
                    "test:secondary" => "second-secret",
                    _ => throw new InvalidOperationException($"Unexpected re-parsed key: {key}")
                });
            },
            CancellationToken.None);

        result.Should().Be("Host=db;Password=p@ss{test:injected}${test:other};Backup=second-secret");
        calls.Should().Equal("test:primary", "test:secondary");
    }

    [SecurityTest]
    [Fact]
    public async Task AzureKeyVaultResolver_Transient429And503_RetriesAndReturnsSecret()
    {
        var previousTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        var previousClient = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var previousSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", "tenant-1");
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", "client-1");
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", "secret-1");

        try
        {
            var secretCalls = 0;
            var retryDelays = 0;
            using var secretHandler = new StubHandler(request =>
            {
                if (request.RequestUri!.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"access_token":"tok-1","expires_in":3600}""", Encoding.UTF8, "application/json")
                    };
                }

                secretCalls++;
                if (secretCalls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                }

                if (secretCalls == 2)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":"kv-resolved"}""", Encoding.UTF8, "application/json")
                };
            });
            using var httpClient = new HttpClient(secretHandler, disposeHandler: false);
            using var metadataHandler = new StubHandler(_ => throw new InvalidOperationException("metadata unused"));
            using var metadataClient = new HttpClient(metadataHandler, disposeHandler: false);
            using var resolver = new AzureKeyVaultResolver(
                httpClient,
                metadataClient,
                NullLogger<AzureKeyVaultResolver>.Instance,
                (_, _) =>
                {
                    retryDelays++;
                    return Task.CompletedTask;
                });

            var resolved = await resolver.ResolveSecretAsync("azure:keyvault:myvault:db-password");

            resolved.Should().Be("kv-resolved");
            secretCalls.Should().Be(3);
            retryDelays.Should().Be(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", previousTenant);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", previousClient);
            Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", previousSecret);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
