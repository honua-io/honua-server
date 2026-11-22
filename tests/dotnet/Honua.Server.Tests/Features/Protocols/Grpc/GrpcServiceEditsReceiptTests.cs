// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Protocols.Grpc;
using Honua.TestKit.Attributes;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

public sealed class GrpcServiceEditsReceiptTests
{
    [UnitTest]
    public void Fingerprint_MapInsertionOrderAndKey_DoNotChangeRequestIdentity()
    {
        var first = Request("a", "b");
        var reordered = Request("b", "a");
        reordered.IdempotencyKey = "another key";

        GrpcServiceEditsReceipts.Fingerprint(first).Should().Be(GrpcServiceEditsReceipts.Fingerprint(reordered));
        reordered.Edits[0].Adds[0].Attributes["a"].StringValue = "changed";
        GrpcServiceEditsReceipts.Fingerprint(first).Should().NotBe(GrpcServiceEditsReceipts.Fingerprint(reordered));
    }

    [UnitTest]
    public void Fingerprint_LayerOrderAndRollbackMode_ChangeRequestIdentity()
    {
        var first = Request("a", "b");
        first.Edits.Add(new Proto.ServiceLayerEdits { LayerId = 1 });
        var reversed = first.Clone();
        reversed.Edits.Clear();
        reversed.Edits.Add(first.Edits.Reverse());
        GrpcServiceEditsReceipts.Fingerprint(first).Should().NotBe(GrpcServiceEditsReceipts.Fingerprint(reversed));
        reversed = first.Clone();
        reversed.RollbackOnFailure = !first.RollbackOnFailure;
        GrpcServiceEditsReceipts.Fingerprint(first).Should().NotBe(GrpcServiceEditsReceipts.Fingerprint(reversed));
    }

    [UnitTest]
    public async Task EnterAsync_NewNode_ReadsOrderedResultAndFingerprintFromDurableReceipt()
    {
        var fingerprint = GrpcServiceEditsReceipts.Fingerprint(Request("a", "b"));
        var response = new Proto.ApplyServiceEditsResponse
        {
            Results =
            {
                new Proto.ServiceLayerEditResults { LayerId = 7 },
                new Proto.ServiceLayerEditResults { LayerId = 3 }
            }
        };
        using var node = new GrpcServiceEditsReceipts(new StoredReceipt(
            GrpcServiceEditsReceipts.EncodeReceipt(response, fingerprint)));

        using var entry = await node.EnterAsync("scope", CancellationToken.None);

        entry.OutcomeUnknown.Should().BeFalse();
        entry.Response.Should().Be(response);
        entry.RequestFingerprint.Should().Be(fingerprint);
    }

    [UnitTheory]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 2, 0 })]
    public async Task EnterAsync_InvalidDurableReceipt_PreventsExecution(byte[] payload)
    {
        using var node = new GrpcServiceEditsReceipts(new StoredReceipt(payload));

        using var entry = await node.EnterAsync("scope", CancellationToken.None);

        entry.Response.Should().BeNull();
        entry.OutcomeUnknown.Should().BeTrue();
    }

    private sealed class StoredReceipt(byte[] payload) : IFeatureEditIdempotencyReceipts
    {
        public ValueTask<ReadOnlyMemory<byte>?> TryReadAsync(string scope, CancellationToken cancellationToken = default)
            => new((ReadOnlyMemory<byte>?)payload);
    }

    private static Proto.ApplyServiceEditsRequest Request(string first, string second)
    {
        var request = new Proto.ApplyServiceEditsRequest { ServiceId = "test", IdempotencyKey = "key" };
        request.Edits.Add(new Proto.ServiceLayerEdits
        {
            LayerId = 0,
            Adds = { new Proto.Feature
            {
                Attributes =
                {
                    [first] = new Proto.AttributeValue { StringValue = first },
                    [second] = new Proto.AttributeValue { StringValue = second }
                }
            } }
        });
        return request;
    }
}
