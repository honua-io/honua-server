// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Org.BouncyCastle.Crypto.Parameters;
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

        var sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .ToArray();
        var syntaxTrees = sourceFiles
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "TranscriptProvenanceSigningBoundary",
            syntaxTrees,
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Ed25519PrivateKeyParameters).Assembly.Location),
            ]);

        var privateKeyConstructionFiles = syntaxTrees
            .Where(tree => ContainsPrivateKeyConstruction(compilation.GetSemanticModel(tree), tree))
            .Select(tree => tree.FilePath)
            .Select(Path.GetFullPath)
            .ToArray();

        privateKeyConstructionFiles.Should().ContainSingle(
            "production Ed25519 private keys may only be constructed after secret-reference resolution");
        privateKeyConstructionFiles.Single().Should().Be(Path.GetFullPath(signerPath));
    }

    [Theory]
    [InlineData("var key = new Ed25519PrivateKeyParameters(seed, 0);")]
    [InlineData("var key = new Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters (seed, 0);")]
    [InlineData("Ed25519PrivateKeyParameters key = new(seed, 0);")]
    public void PrivateKeyConstructionDetection_IsIndependentOfSourceSpelling(string construction)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            $$"""
            using Org.BouncyCastle.Crypto.Parameters;
            internal sealed class Example
            {
                private static void Create(byte[] seed)
                {
                    {{construction}}
                }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "PrivateKeyConstructionDetection",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Ed25519PrivateKeyParameters).Assembly.Location),
            ]);

        ContainsPrivateKeyConstruction(compilation.GetSemanticModel(syntaxTree), syntaxTree).Should().BeTrue();
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

    private static bool ContainsPrivateKeyConstruction(SemanticModel semanticModel, SyntaxTree syntaxTree) =>
        syntaxTree.GetRoot()
            .DescendantNodes()
            .OfType<BaseObjectCreationExpressionSyntax>()
            .Select(node => semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol)
            .Any(constructor => constructor?.ContainingType.ToDisplayString() ==
                "Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters");
}
