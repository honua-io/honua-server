// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Services;

namespace Honua.Core.Tests.Features.Spec;

/// <summary>
/// Proves the content-hash cache key contract: the hash is deterministic and
/// changes when any participant changes. These are the tests the cache and the
/// rest of the apply engine rely on.
/// </summary>
public class SpecContentHashCalculatorTests
{
    private const string GrammarV1 = "grammar/1.0";
    private const string GrammarV2 = "grammar/2.0";
    private const string FamilyV1 = "family/1.0";
    private const string FamilyV2 = "family/2.0";

    [Fact]
    public void SameInputs_ProduceSameHash()
    {
        var node = new CanonicalSpecNode
        {
            Id = "n1",
            Kind = SpecResourceKind.Compute,
            Op = "compute.buffer",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["distance"] = "100" }
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, new Dictionary<string, string>());

        Assert.Equal(hashA, hashB);
        Assert.Matches("^[0-9a-f]{64}$", hashA);
    }

    [Fact]
    public void DifferentGrammarVersion_ProducesDifferentHash()
    {
        var node = MakeComputeNode("n1");

        var hashV1 = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, new Dictionary<string, string>());
        var hashV2 = SpecContentHashCalculator.Compute(GrammarV2, FamilyV1, node, new Dictionary<string, string>());

        Assert.NotEqual(hashV1, hashV2);
    }

    [Fact]
    public void DifferentProcessFamilyVersion_ProducesDifferentHash()
    {
        var node = MakeComputeNode("n1");

        var hashV1 = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, new Dictionary<string, string>());
        var hashV2 = SpecContentHashCalculator.Compute(GrammarV1, FamilyV2, node, new Dictionary<string, string>());

        Assert.NotEqual(hashV1, hashV2);
    }

    [Fact]
    public void DifferentCanonicalFragment_ProducesDifferentHash()
    {
        var nodeA = MakeComputeNode("n1") with { CanonicalFragment = "{\"op\":\"buffer\",\"distance\":100}" };
        var nodeB = MakeComputeNode("n1") with { CanonicalFragment = "{\"op\":\"buffer\",\"distance\":250}" };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeA, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeB, new Dictionary<string, string>());

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void DifferentParameters_ProducesDifferentHash()
    {
        var nodeA = MakeComputeNode("n1") with
        {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["distance"] = "100" }
        };
        var nodeB = MakeComputeNode("n1") with
        {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["distance"] = "250" }
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeA, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeB, new Dictionary<string, string>());

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void ParameterOrder_DoesNotAffectHash()
    {
        var nodeA = MakeComputeNode("n1") with
        {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a"] = "1",
                ["b"] = "2",
                ["c"] = "3"
            }
        };
        var nodeB = MakeComputeNode("n1") with
        {
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["c"] = "3",
                ["a"] = "1",
                ["b"] = "2"
            }
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeA, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeB, new Dictionary<string, string>());

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void InputHashOrder_DoesNotAffectHash()
    {
        var node = MakeComputeNode("n1");

        var inputsA = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["leftInput"] = "aaa111",
            ["rightInput"] = "bbb222"
        };
        var inputsB = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rightInput"] = "bbb222",
            ["leftInput"] = "aaa111"
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, inputsA);
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, inputsB);

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void DifferentInputHashes_ProduceDifferentHash()
    {
        var node = MakeComputeNode("n1");

        var inputsA = new Dictionary<string, string>(StringComparer.Ordinal) { ["left"] = "aaa111" };
        var inputsB = new Dictionary<string, string>(StringComparer.Ordinal) { ["left"] = "ccc333" };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, inputsA);
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, node, inputsB);

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void SourcePinChange_WithoutCanonicalFragment_ProducesDifferentHash()
    {
        var nodeA = MakeComputeNode("n1") with
        {
            SourcePins = new Dictionary<string, string>(StringComparer.Ordinal) { ["roads"] = "v1" }
        };
        var nodeB = MakeComputeNode("n1") with
        {
            SourcePins = new Dictionary<string, string>(StringComparer.Ordinal) { ["roads"] = "v2" }
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeA, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeB, new Dictionary<string, string>());

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void CanonicalFragment_SupersedesParametersAndSourcePins()
    {
        // Fragment is the canonical form; when it is present, the hash is
        // derived from it, and any local parameter/source-pin diffs stop
        // mattering. Upstream canonicalisation is responsible for encoding
        // parameter/source-pin differences INTO the fragment.
        var nodeA = MakeComputeNode("n1") with
        {
            CanonicalFragment = "{\"op\":\"buffer\",\"distance\":100}",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["extra"] = "X" }
        };
        var nodeB = MakeComputeNode("n1") with
        {
            CanonicalFragment = "{\"op\":\"buffer\",\"distance\":100}",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["extra"] = "Y" }
        };

        var hashA = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeA, new Dictionary<string, string>());
        var hashB = SpecContentHashCalculator.Compute(GrammarV1, FamilyV1, nodeB, new Dictionary<string, string>());

        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void HashBytes_ReturnsLowercaseHex()
    {
        var bytes = "hello"u8;
        var hash = SpecContentHashCalculator.HashBytes(bytes);

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        // SHA-256("hello") is a well-known value.
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
    }

    private static CanonicalSpecNode MakeComputeNode(string id) => new()
    {
        Id = id,
        Kind = SpecResourceKind.Compute,
        Op = "compute.buffer"
    };
}
