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

    [Theory]
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
}
