// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the security-critical custody decisions for transcript-provenance signing.
/// Private key construction is deliberately confined to the signer that resolves a
/// secret reference; production code must never create substitute key material.
/// </summary>
public sealed class TranscriptProvenanceSigningBoundaryTests
{
    [Fact]
    public void ProductionSigningKeyHandling_IsConfinedToSecretBackedSigner()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var sourceRoot = ArchitectureTestHelpers.CombinePath(root, "src", "Honua.Ai");
        var signerPath = ArchitectureTestHelpers.CombinePath(
            sourceRoot,
            "Features",
            "StudioAiProxy",
            "StudioAiTranscriptSigner.cs");
        var signerSource = File.ReadAllText(signerPath);

        signerSource.Should().Contain("ISecretProvider? secretProvider");
        signerSource.Should().Contain("secretProvider.IsSecretReference(_options.PrivateKeyReference)");
        signerSource.Should().Contain("secretProvider.GetSecretOrDefaultAsync(");

        var privateKeyConstructionFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("new Ed25519PrivateKeyParameters(", StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .ToArray();

        privateKeyConstructionFiles.Should().ContainSingle(
            "production Ed25519 private keys may only be constructed after secret-reference resolution");
        privateKeyConstructionFiles.Single().Should().Be(Path.GetFullPath(signerPath));
    }

    [Fact]
    public void ProductionSigner_DoesNotGenerateFallbackKeyMaterial()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var signerPath = ArchitectureTestHelpers.CombinePath(
            root,
            "src",
            "Honua.Ai",
            "Features",
            "StudioAiProxy",
            "StudioAiTranscriptSigner.cs");
        var signerSource = File.ReadAllText(signerPath);

        signerSource.Should().NotContain("RandomNumberGenerator");
        signerSource.Should().NotContain("SecureRandom");
        signerSource.Should().NotContain("GenerateSeed");
        signerSource.Should().Contain("return null;",
            "missing or invalid referenced key material must fail closed instead of creating a key");
    }
}
