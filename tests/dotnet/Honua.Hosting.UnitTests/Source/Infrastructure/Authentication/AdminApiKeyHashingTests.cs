// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class AdminApiKeyHashingTests
{
    [UnitTest]
    public void Verify_AcceptsBothFormats_AndRejectsDifferentMaterial()
    {
        const string material = "test-key-material";
        var salt = RandomNumberGenerator.GetBytes(32);
        var saltedHash = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(material));
        AdminApiKeyHashing.Verify(material, AdminApiKeyHashing.HashLegacy(material), null).Should().BeTrue();
        AdminApiKeyHashing.Verify(material, saltedHash, salt).Should().BeTrue();
        AdminApiKeyHashing.Verify(material + "changed", saltedHash, salt).Should().BeFalse();
        AdminApiKeyHashing.Verify(material, saltedHash, RandomNumberGenerator.GetBytes(32)).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(33)]
    public void Verify_WithInvalidSalt_RejectsLegacyDigest(int length)
    {
        const string material = "test-key-material";
        AdminApiKeyHashing.Verify(material, AdminApiKeyHashing.HashLegacy(material), new byte[length]).Should().BeFalse();
    }

    [UnitTest]
    public void Verify_WithInvalidDigest_RejectsBothFormats()
    {
        AdminApiKeyHashing.Verify("material", [], null).Should().BeFalse();
        AdminApiKeyHashing.Verify("material", new byte[31], new byte[32]).Should().BeFalse();
    }

    [UnitTest]
    public void HashSalted_EqualMaterial_ProducesDistinctStoredValues_ThatBothVerify()
    {
        const string material = "test-key-material";
        var first = AdminApiKeyHashing.HashSalted(material);
        var second = AdminApiKeyHashing.HashSalted(material);

        first.Salt.Should().HaveCount(AdminApiKeyHashing.SaltSizeInBytes).And.NotEqual(second.Salt);
        first.Hash.Should().NotEqual(second.Hash).And.NotEqual(AdminApiKeyHashing.HashLegacy(material));
        AdminApiKeyHashing.Verify(material, first.Hash, first.Salt).Should().BeTrue();
        AdminApiKeyHashing.Verify(material, second.Hash, second.Salt).Should().BeTrue();
        AdminApiKeyHashing.Verify(material, first.Hash, second.Salt).Should().BeFalse();
    }

    [UnitTest]
    public async Task InMemoryStore_CreatedRotatedAndRegisteredKeys_AreStoredSalted()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("salted", ["admin:read"], null, null, CancellationToken.None);
        var rotated = await store.RotateAsync(created.Record.Id, CancellationToken.None);
        var registered = store.RegisterKnownMaterial("hnua_known-test-material", "known", ["admin:read"], null, null);

        foreach (var result in new[] { created, rotated!, registered })
        {
            result.Record.KeySalt.Should().HaveCount(AdminApiKeyHashing.SaltSizeInBytes);
            result.Record.KeyHash.Should().Equal(HMACSHA256.HashData(result.Record.KeySalt!, Encoding.UTF8.GetBytes(result.Key)));
        }

        (await store.ValidateAsync(created.Key, CancellationToken.None)).Should().BeNull("rotation revokes the previous key");
        (await store.ValidateAsync(rotated!.Key, CancellationToken.None)).Should().NotBeNull();
        (await store.ValidateAsync(registered.Key, CancellationToken.None)).Should().NotBeNull();
    }
}
