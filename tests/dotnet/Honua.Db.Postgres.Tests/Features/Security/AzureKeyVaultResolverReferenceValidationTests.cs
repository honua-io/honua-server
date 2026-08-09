// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public class AzureKeyVaultResolverReferenceValidationTests
{
    [SecurityTest]
    [Theory]
    [InlineData("myvault")]
    [InlineData("my-vault-01")]
    [InlineData("Vault123")]
    public void IsValidVaultName_ValidNames_ReturnsTrue(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidVaultName(value);

        Assert.True(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("")]
    [InlineData("-myvault")]
    [InlineData("myvault-")]
    [InlineData("my.vault")]
    [InlineData("my/vault")]
    [InlineData("my_vault")]
    [InlineData("averyveryveryveryverylongvaultname")]
    public void IsValidVaultName_InvalidNames_ReturnsFalse(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidVaultName(value);

        Assert.False(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("secret-name")]
    [InlineData("secret123")]
    [InlineData("A")]
    public void IsValidSecretName_ValidNames_ReturnsTrue(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidSecretName(value);

        Assert.True(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("secret/name")]
    [InlineData("secret?query=1")]
    [InlineData("secret_name")]
    public void IsValidSecretName_InvalidNames_ReturnsFalse(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidSecretName(value);

        Assert.False(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("A1B2C3")]
    public void IsValidSecretVersion_ValidValues_ReturnsTrue(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidSecretVersion(value);

        Assert.True(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("")]
    [InlineData("../version")]
    [InlineData("version-with-dash")]
    [InlineData("version?x=1")]
    public void IsValidSecretVersion_InvalidValues_ReturnsFalse(string value)
    {
        var isValid = AzureKeyVaultResolver.IsValidSecretVersion(value);

        Assert.False(isValid);
    }

    [SecurityTest]
    [Theory]
    [InlineData("azure:keyvault:myvault:my-secret")]
    [InlineData("azure:keyvault:myvault:my-secret:0123456789abcdef0123456789abcdef")]
    public async Task CanResolveSecretAsync_ValidReference_ReturnsTrue(string secretRef)
    {
        using var resolver = CreateResolver();

        var canResolve = await resolver.CanResolveSecretAsync(secretRef);

        Assert.True(canResolve);
    }

    [SecurityTest]
    [Theory]
    [InlineData("azure:keyvault:my.vault:my-secret")]
    [InlineData("azure:keyvault:myvault:../keys/my-key")]
    [InlineData("azure:keyvault:myvault:my-secret:../version")]
    [InlineData("azure:keyvault:myvault:my-secret?api-version=1")]
    public async Task CanResolveSecretAsync_MaliciousReference_ReturnsFalse(string secretRef)
    {
        using var resolver = CreateResolver();

        var canResolve = await resolver.CanResolveSecretAsync(secretRef);

        Assert.False(canResolve);
    }

    private static AzureKeyVaultResolver CreateResolver()
        => new(new StubHttpClientFactory(), NullLogger<AzureKeyVaultResolver>.Instance);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
