// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Redis;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// honua-server#6753: a Lambda-hosted content-version read whose API-key lookup hits a Redis
/// pipe that was already completed must retry that lookup and return the version. The journey
/// cell recorded HTTP 503 for <c>GET /api/v1/studio/content-items/{itemId}/versions/{versionId}</c>
/// when the fault escaped as an unhandled <see cref="InvalidOperationException"/>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class LambdaContentVersionReadTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private CompletedReaderAdminApiKeyStore _keys = null!;
    private string _managedKey = null!;

    public async Task InitializeAsync()
    {
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting(ControlPlaneEventHandlerHost.LambdaFunctionNameVariable, "honua-api");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IAdminApiKeyStore>();
                services.AddSingleton(sp => new CompletedReaderAdminApiKeyStore(sp.GetRequiredService<IHttpContextAccessor>()));
                services.AddSingleton<IAdminApiKeyStore>(sp => sp.GetRequiredService<CompletedReaderAdminApiKeyStore>());
            });
        await _fixture.InitializeAsync();
        _keys = _fixture.Services.GetRequiredService<CompletedReaderAdminApiKeyStore>();
        var created = await _keys.CreateAsync("journey-operator", ["admin:*"], null, "lambda-test", CancellationToken.None);
        _managedKey = created.Key;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions/{versionId}")]
    public async Task GetContentVersion_LambdaHostCompletedReaderOnce_ReturnsOk()
    {
        _fixture.Services.GetRequiredService<IConfiguration>()[ControlPlaneEventHandlerHost.LambdaFunctionNameVariable]
            .Should().Be("honua-api");
        ControlPlaneEventHandlerHost.IsLambda(_fixture.Services.GetRequiredService<IConfiguration>()).Should().BeTrue();

        await using var scope = _fixture.Services.CreateAsyncScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        using var body = JsonDocument.Parse("""{"where":"1=1"}""");
        var draft = await lifecycle.CreateDraftAsync(new CreateStudioPackageDraftCommand
        {
            PackageKey = $"lambda-read-{Guid.NewGuid():N}",
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Query,
                SchemaVersion = "1.0",
                Format = "studio_query_package.v1",
                Body = body.RootElement.Clone(),
            },
        });
        var version = await lifecycle.SaveDraftAsVersionAsync(draft.DraftId, "lambda thaw", "test", draft.Generation);
        version.Should().NotBeNull();

        using var client = _fixture.CreateClient(http => http.DefaultRequestHeaders.Add("X-API-Key", _managedKey));
        var response = await client.GetAsync(
            $"/api/v1/studio/content-items/{version!.ItemId:D}/versions/{version.VersionId:D}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _keys.FaultsInjected.Should().Be(1);
    }

    /// <summary>
    /// In-memory key registry that throws the completed-reader fault once, on the content-version
    /// GET the Lambda journey failed. Every other call, including the retry, reaches the inner store.
    /// </summary>
    private sealed class CompletedReaderAdminApiKeyStore(IHttpContextAccessor httpContextAccessor) : IAdminApiKeyStore
    {
        private readonly InMemoryAdminApiKeyStore _inner = new();
        private int _armed = 1;

        public int FaultsInjected { get; private set; }

        public Task<AdminApiKeyValidationResult?> ValidateAsync(string keyMaterial, CancellationToken cancellationToken)
        {
            var http = httpContextAccessor.HttpContext;
            var path = http?.Request.Path.Value;
            if (http is not null
                && HttpMethods.IsGet(http.Request.Method)
                && path?.Contains("/versions/", StringComparison.Ordinal) == true
                && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                FaultsInjected++;
                throw new InvalidOperationException(RedisClientFault.CompletedReaderMessage);
            }

            return _inner.ValidateAsync(keyMaterial, cancellationToken);
        }

        public Task<IReadOnlyList<AdminApiKeyRecord>> ListAsync(CancellationToken cancellationToken)
            => _inner.ListAsync(cancellationToken);

        public Task<AdminApiKeyCreateResult> CreateAsync(
            string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, string? createdBy, CancellationToken cancellationToken)
            => _inner.CreateAsync(name, permissions, expiresAt, createdBy, cancellationToken);

        public Task<AdminApiKeyCreateResult> CreateAsync(
            string name, IReadOnlyList<string> permissions, DateTimeOffset? expiresAt, string? createdBy, AdminApiKeyRestrictions? restrictions, CancellationToken cancellationToken)
            => _inner.CreateAsync(name, permissions, expiresAt, createdBy, restrictions, cancellationToken);

        public Task<AdminApiKeyRecord?> GetAsync(Guid id, CancellationToken cancellationToken)
            => _inner.GetAsync(id, cancellationToken);

        public Task<AdminApiKeyCreateResult?> RotateAsync(Guid id, CancellationToken cancellationToken)
            => _inner.RotateAsync(id, cancellationToken);

        public Task<AdminApiKeyRecord?> RevokeAsync(Guid id, CancellationToken cancellationToken)
            => _inner.RevokeAsync(id, cancellationToken);

        public Task<AdminApiKeyRecord?> UpdateRestrictionsAsync(Guid id, AdminApiKeyRestrictions restrictions, CancellationToken cancellationToken)
            => _inner.UpdateRestrictionsAsync(id, restrictions, cancellationToken);
    }
}
