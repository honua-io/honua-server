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
            new SecretTemplateExpansionRequest
            {
                ConnectionStringTemplate = "Host=db;Password=${test:primary};Backup={test:secondary}",
                DirectPrefix = "test:",
                CanResolve = static key => key.StartsWith("test:", StringComparison.OrdinalIgnoreCase),
                ResolveSecretAsync = (key, _) =>
                {
                    calls.Add(key);
                    return Task.FromResult<string?>(key switch
                    {
                        "test:primary" => "p@ss{test:injected}${test:other}",
                        "test:secondary" => "second-secret",
                        _ => throw new InvalidOperationException($"Unexpected re-parsed key: {key}")
                    });
                }
            },
            CancellationToken.None);

        result.Should().Be("Host=db;Password=p@ss{test:injected}${test:other};Backup=second-secret");
        calls.Should().Equal("test:primary", "test:secondary");
    }

    [SecurityTest]
    [Fact]
    public async Task AzureKeyVaultResolver_Transient429And503_RetriesAndReturnsSecret()
    {
        using var credentialsScope = UseAzureClientCredentials("tenant-1", "client-1", "secret-1");
        var secretCalls = 0;
        var retryDelays = 0;
        using var secretHandler = new StubHandler(request => BuildKeyVaultResponse(request, () => ++secretCalls));
        using var httpClient = new HttpClient(secretHandler, disposeHandler: false);
        using var metadataHandler = new StubHandler(_ => throw new InvalidOperationException("metadata unused"));
        using var metadataClient = new HttpClient(metadataHandler, disposeHandler: false);
        using var resolver = new AzureKeyVaultResolver(new AzureKeyVaultResolverOptions
        {
            HttpClient = httpClient,
            MetadataClient = metadataClient,
            Logger = NullLogger<AzureKeyVaultResolver>.Instance,
            RetryDelay = (_, _) =>
            {
                retryDelays++;
                return Task.CompletedTask;
            }
        });

        var resolved = await resolver.ResolveSecretAsync("azure:keyvault:myvault:db-password");

        resolved.Should().Be("kv-resolved");
        secretCalls.Should().Be(3);
        retryDelays.Should().Be(2);
    }

    private static HttpResponseMessage BuildKeyVaultResponse(HttpRequestMessage request, Func<int> nextSecretCall)
    {
        if (request.RequestUri!.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse("""{"access_token":"tok-1","expires_in":3600}""");
        }

        return nextSecretCall() switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            2 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => JsonResponse("""{"value":"kv-resolved"}""")
        };
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static EnvRestoreScope UseAzureClientCredentials(string tenantId, string clientId, string clientSecret)
    {
        var scope = new EnvRestoreScope(
            ("AZURE_TENANT_ID", Environment.GetEnvironmentVariable("AZURE_TENANT_ID")),
            ("AZURE_CLIENT_ID", Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")),
            ("AZURE_CLIENT_SECRET", Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET")));
        Environment.SetEnvironmentVariable("AZURE_TENANT_ID", tenantId);
        Environment.SetEnvironmentVariable("AZURE_CLIENT_ID", clientId);
        Environment.SetEnvironmentVariable("AZURE_CLIENT_SECRET", clientSecret);
        return scope;
    }

    private sealed class EnvRestoreScope(params (string Key, string? Value)[] previous) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
