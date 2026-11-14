// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Collection("Security")]
public sealed class PortalTokenSecretAuthorityTests
{
    private const string Password = "Initial-Password-829!";

    [Theory]
    [UnitTest]
    [InlineData("aws", "rotated")]
    [InlineData("aws", "removed")]
    [InlineData("aws", "unavailable")]
    [InlineData("azure", "rotated")]
    [InlineData("azure", "removed")]
    [InlineData("azure", "unavailable")]
    public async Task IsStillValidAsync_CloudAuthorityChangesAfterCacheWarmup_Refuses(string provider, string change)
    {
        using var environment = new CredentialEnvironment();
        using var handler = new AuthorityHandler(provider);
        using var client = new HttpClient(handler);
        using var metadata = new HttpClient(new TokenHandler());
        var factory = new ClientFactory(client, metadata);
        using var cloud = provider == "aws"
            ? (IDisposable)new AwsSecretsManagerResolver(factory, NullLogger<AwsSecretsManagerResolver>.Instance)
            : new AzureKeyVaultResolver(factory, NullLogger<AzureKeyVaultResolver>.Instance);
        var resolver = new CompositeSecretResolver([(IConnectionSecretResolver)cloud], NullLogger<CompositeSecretResolver>.Instance);
        var reference = provider == "aws" ? "aws:secretsmanager:portal/password" : "azure:keyvault:testvault:password";
        (await resolver.ResolveSecretAsync(reference)).Should().Be(Password);
        using var services = new ServiceCollection()
            .AddSingleton<IConnectionSecretResolver>(resolver)
            .AddSingleton<IOptions<ApiKeyAuthenticationOptions>>(Options.Create(new ApiKeyAuthenticationOptions
            {
                AdminPassword = reference,
                EnvironmentName = "Production",
            }))
            .BuildServiceProvider();
        var validator = new PortalTokenSourceValidator(services);
        var source = new PortalTokenSourceRecord
        {
            Kind = PortalCredentialSourceKind.AdminPassword,
            Version = PortalCredentialSourceVersion.ForAdminPassword(Password),
        };
        (await validator.IsStillValidAsync(source, CancellationToken.None)).Should().BeTrue();
        handler.Change = change;

        (await validator.IsStillValidAsync(source, CancellationToken.None)).Should().BeFalse();
        handler.SecretReads.Should().BeGreaterThanOrEqualTo(3);
    }

    private sealed class AuthorityHandler(string provider) : HttpMessageHandler
    {
        public string? Change { get; set; }
        public int SecretReads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return Task.FromResult(Json("{\"access_token\":\"test-token\",\"expires_in\":3600}"));
            }

            SecretReads++;
            if (Change == "unavailable")
            {
                throw new HttpRequestException("Authority unavailable");
            }

            if (Change == "removed")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var value = Change == "rotated" ? "Rotated-Password-673!" : Password;
            var property = provider == "aws" ? "SecretString" : "value";
            return Task.FromResult(Json($"{{\"{property}\":\"{value}\"}}"));
        }
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Json("{\"access_token\":\"test-token\",\"expires_in\":3600}"));
    }

    private sealed class ClientFactory(HttpClient secrets, HttpClient metadata) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name is "AwsSecretsManager" or "AzureKeyVault" ? secrets : metadata;
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class CredentialEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new(StringComparer.Ordinal);

        public CredentialEnvironment()
        {
            Set("AWS_ACCESS_KEY_ID", "AKIA_TEST_ACCESS_KEY");
            Set("AWS_SECRET_ACCESS_KEY", "test-secret-key");
            Set("AWS_SESSION_TOKEN", null);
            Set("AWS_REGION", "us-east-1");
            Set("AZURE_TENANT_ID", "test-tenant");
            Set("AZURE_CLIENT_ID", "test-client");
            Set("AZURE_CLIENT_SECRET", "test-client-secret");
        }

        private void Set(string name, string? value)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
