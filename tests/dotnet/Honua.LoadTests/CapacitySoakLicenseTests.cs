// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Xunit;

namespace Honua.LoadTests;

[Collection("Capacity soak console")]
public sealed class CapacitySoakLicenseTests
{
    [UnitTest]
    public void Main_WriteSoakLicense_WritesSignedLicenseWithoutRunningLoad()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        var originalOutput = Console.Out;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            var before = DateTimeOffset.UtcNow;
            Console.SetOut(output);
            Assert.Equal(0, Program.Main(["--write-soak-license", path]));
            VerifyLicense(path, output.ToString().Trim(), before);
        }
        finally
        {
            Console.SetOut(originalOutput);
            File.Delete(path);
        }
    }

    [UnitTest]
    public void WriteLicenseAndReturnTrustedKey_RepeatedRuns_UseDistinctSigningKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "license.json");
            var before = DateTimeOffset.UtcNow;
            var firstKey = CapacitySoakLicense.WriteLicenseAndReturnTrustedKey(output);
            VerifyLicense(output, firstKey, before);
            var secondKey = CapacitySoakLicense.WriteLicenseAndReturnTrustedKey(output);
            VerifyLicense(output, secondKey, before);
            Assert.NotEqual(firstKey, secondKey);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyLicense(string path, string trustedKey, DateTimeOffset before)
    {
        using var envelope = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, envelope.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("soak", envelope.RootElement.GetProperty("keyId").GetString());
        var payloadBytes = Base64Url.DecodeFromChars(envelope.RootElement.GetProperty("payload").GetString()!);
        var signature = Base64Url.DecodeFromChars(envelope.RootElement.GetProperty("signature").GetString()!);
        Assert.StartsWith("base64url:", trustedKey, StringComparison.Ordinal);
        var publicKey = new Ed25519PublicKeyParameters(Base64Url.DecodeFromChars(trustedKey["base64url:".Length..]), 0);
        var verifier = new Ed25519Signer();
        verifier.Init(false, publicKey);
        verifier.BlockUpdate(payloadBytes, 0, payloadBytes.Length);
        Assert.True(verifier.VerifySignature(signature));

        using var payload = JsonDocument.Parse(payloadBytes);
        Assert.Equal("Pro", payload.RootElement.GetProperty("edition").GetString());
        var expiresAt = payload.RootElement.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.InRange(expiresAt, before.AddDays(2), DateTimeOffset.UtcNow.AddDays(2));
        var entitlements = payload.RootElement.GetProperty("entitlements").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Equal(LicenseTestSupport.ActiveEntitlementsFor(HonuaEdition.Pro), entitlements);
    }
}

[CollectionDefinition("Capacity soak console", DisableParallelization = true)]
public sealed class CapacitySoakConsoleCollection
{
}
