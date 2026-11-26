// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Infrastructure.Services;

public sealed partial class TemporaryFileServiceTests
{
    [UnitTest]
    public async Task TemporaryFileOwnership_IsolatesDistinctApiKeysIssuersAndTenantsAndFailsClosedWithoutIdentity()
    {
        using var localService = CreateService(new TemporaryFileOptions
        {
            StorageDirectory = _storageDirectory,
            DefaultExpiration = TimeSpan.FromMinutes(5)
        });
        var cloudStorage = new FakeCloudFileStorage(CloudStorageProvider.AzureBlob);
        using var cloudService = CreateCloudAwareService(
            new TemporaryFileOptions
            {
                StorageDirectory = Path.Join(_storageDirectory, "cloud"),
                BaseUrl = "/temp",
                DefaultExpiration = TimeSpan.FromMinutes(5)
            },
            cloudStorage,
            leases: new InMemoryLeaseStore());

        await AssertOwnershipIsolationAsync(localService);
        await AssertOwnershipIsolationAsync(cloudService);
    }

    private static async Task AssertOwnershipIsolationAsync(ITemporaryFileService service)
    {
        var managedKeyA = CreateManagedAdminApiKeyPrincipal(Guid.NewGuid());
        var managedKeyB = CreateManagedAdminApiKeyPrincipal(Guid.NewGuid());
        var bootstrapAdmin = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "admin"), new Claim("auth_type", "admin")],
            AuthenticationExtensions.ApiKeyScheme));

        var keyFile = Path.GetFileName(await service.StoreTemporaryFileAsync([1, 2], "image/png", principal: managedKeyA));
        (await service.GetTemporaryFileAsync(keyFile, managedKeyA)).Should().NotBeNull();
        (await service.GetTemporaryFileAsync(keyFile, managedKeyB)).Should().BeNull("distinct managed admin keys must not collide");
        (await service.GetTemporaryFileAsync(keyFile, bootstrapAdmin)).Should().BeNull("bootstrap admin must not collide with managed admin keys");

        var oidcTenantA = CreateOidcPrincipal("https://idp-a.example", "tenant-a");
        var oidcOtherIssuer = CreateOidcPrincipal("https://idp-b.example", "tenant-a");
        var oidcOtherTenant = CreateOidcPrincipal("https://idp-a.example", "tenant-b");

        var oidcFile = Path.GetFileName(await service.StoreTemporaryFileAsync([3, 4], "image/png", principal: oidcTenantA));
        (await service.GetTemporaryFileAsync(oidcFile, oidcTenantA)).Should().NotBeNull();
        (await service.GetTemporaryFileAsync(oidcFile, oidcOtherIssuer)).Should().BeNull("identical sub under a different issuer must not match");
        (await service.GetTemporaryFileAsync(oidcFile, oidcOtherTenant)).Should().BeNull("identical sub under a different tenant must not match");

        var anonymousAuthenticated = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        var act = () => service.StoreTemporaryFileAsync([5, 6], "image/png", principal: anonymousAuthenticated);
        await act.Should().ThrowAsync<InvalidOperationException>("authenticated principals without a unique identity must fail closed");
    }

    private static ClaimsPrincipal CreateOidcPrincipal(string issuer, string tenantId)
        => new(new ClaimsIdentity(
            [new Claim("sub", "same-sub"), new Claim("iss", issuer), new Claim("tid", tenantId)],
            "Bearer"));

    private static ClaimsPrincipal CreateManagedAdminApiKeyPrincipal(Guid apiKeyId)
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "admin-api-key"),
                new Claim("api_key_id", apiKeyId.ToString("D")),
            ],
            AuthenticationExtensions.ApiKeyScheme));
}
