// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.SensorThings.Streaming;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Admission caps for the observation stream (#4198): one principal or tenant must never be
/// able to pin the node-wide session budget and lock every other subscriber out.
/// </summary>
public sealed class ObservationStreamAdmissionTests
{
    private static readonly ObservationStreamScope TenantA = new("tenant-a", "schema_a");
    private static readonly ObservationStreamScope TenantB = new("tenant-b", "schema_b");
    private static readonly ObservationStreamScope TenantC = new("tenant-c", "schema_c");

    private static ObservationStreamSessionManager CreateManager(int perPrincipal, int perTenant, int node) =>
        new(NullLogger<ObservationStreamSessionManager>.Instance, redis: null, new ObservationStreamOptions
        {
            MaxSessionsPerPrincipal = perPrincipal,
            MaxSessionsPerTenant = perTenant,
            MaxConcurrentSessions = node
        });

    private static ObservationStreamSession? Admit(
        ObservationStreamSessionManager manager,
        ObservationStreamScope scope,
        string? principal,
        out ObservationStreamAdmissionLimit rejectedBy) =>
        manager.TryCreateSession("SSE", null, scope, principal, out rejectedBy);

    [UnitTest]
    public void TryCreateSession_EachCapRefusesExactlyAtItsLimit_AndNamesTheCapHit()
    {
        using var manager = CreateManager(perPrincipal: 2, perTenant: 3, node: 4);
        {
            // Principal cap: the third session for one credential is refused while its tenant
            // (2 of 3) and the node (2 of 4) still have room.
            using var alice1 = Admit(manager, TenantA, "alice", out _);
            using var alice2 = Admit(manager, TenantA, "alice", out _);
            Assert.NotNull(alice1);
            Assert.NotNull(alice2);
            Assert.Null(Admit(manager, TenantA, "alice", out var aliceLimit));
            Assert.Equal(ObservationStreamAdmissionLimit.Principal, aliceLimit);

            // A different principal in the same tenant is still admitted: alice cannot lock bob out.
            using var bob = Admit(manager, TenantA, "bob", out var bobLimit);
            Assert.NotNull(bob);
            Assert.Equal(ObservationStreamAdmissionLimit.None, bobLimit);

            // Tenant cap: tenant-a now holds 3 of 3, so a fresh principal there is refused.
            Assert.Null(Admit(manager, TenantA, "carol", out var carolLimit));
            Assert.Equal(ObservationStreamAdmissionLimit.Tenant, carolLimit);

            // Another tenant is unaffected by tenant-a's saturation; this takes the last node slot.
            using var aliceElsewhere = Admit(manager, TenantB, "alice", out var otherTenantLimit);
            Assert.NotNull(aliceElsewhere);
            Assert.Equal(ObservationStreamAdmissionLimit.None, otherTenantLimit);
            Assert.Equal(4, manager.SessionCount);

            // Node cap: an idle principal in an idle tenant is refused only because the node is full.
            Assert.Null(Admit(manager, TenantC, "dave", out var nodeLimit));
            Assert.Equal(ObservationStreamAdmissionLimit.Node, nodeLimit);
            Assert.Equal(4, manager.SessionCount);
        }

        // Leaving the scope disposed every held session; each released its slot.
        Assert.Equal(0, manager.SessionCount);
    }

    [UnitTest]
    public void RemoveSession_ReleasesPrincipalTenantAndNodeSlots()
    {
        using var manager = CreateManager(perPrincipal: 1, perTenant: 1, node: 2);
        var first = Admit(manager, TenantA, "alice", out _)!;
        var other = Admit(manager, TenantB, "bob", out _)!;
        Assert.Null(Admit(manager, TenantA, "alice", out var saturated));
        Assert.Equal(ObservationStreamAdmissionLimit.Principal, saturated);

        first.Dispose();
        first.Dispose(); // a double dispose must not release a slot twice

        // All three counters were released by exactly one: alice (1/1), tenant-a (1/1) and the
        // node (2/2) are full again after one re-admission, not over-admitted.
        using var readmitted = Admit(manager, TenantA, "alice", out var readmittedLimit);
        Assert.NotNull(readmitted);
        Assert.Equal(ObservationStreamAdmissionLimit.None, readmittedLimit);
        Assert.Equal(2, manager.SessionCount);
        Assert.Null(Admit(manager, TenantA, "carol", out var tenantFull));
        Assert.Equal(ObservationStreamAdmissionLimit.Tenant, tenantFull);
        Assert.Null(Admit(manager, TenantC, "dave", out var nodeFull));
        Assert.Equal(ObservationStreamAdmissionLimit.Node, nodeFull);
        other.Dispose();
    }

    [UnitTest]
    public void TryCreateSession_SamePrincipalIdInDifferentTenants_IsCountedPerTenant()
    {
        using var manager = CreateManager(perPrincipal: 1, perTenant: 2, node: 8);
        using var inA = Admit(manager, TenantA, "shared-subject", out _);
        using var inB = Admit(manager, TenantB, "shared-subject", out var limit);

        Assert.NotNull(inA);
        Assert.NotNull(inB);
        Assert.Equal(ObservationStreamAdmissionLimit.None, limit);
    }

    [UnitTest]
    public void TryCreateSession_UnidentifiedCallersInATenant_ShareOnePrincipalPartition()
    {
        using var manager = CreateManager(perPrincipal: 2, perTenant: 4, node: 8);
        using var first = Admit(manager, TenantA, null, out _);
        using var second = Admit(manager, TenantA, " ", out _);

        // Callers without a canonical actor cannot mint fresh partitions to evade the cap.
        Assert.Null(Admit(manager, TenantA, string.Empty, out var limit));
        Assert.Equal(ObservationStreamAdmissionLimit.Principal, limit);
        using var identified = Admit(manager, TenantA, "alice", out var identifiedLimit);
        Assert.Equal(ObservationStreamAdmissionLimit.None, identifiedLimit);
    }

    [UnitTest]
    public void TryCreateSession_UntenantedCallersShareOneTenantPartition()
    {
        using var manager = CreateManager(perPrincipal: 1, perTenant: 2, node: 8);
        using var first = Admit(manager, new ObservationStreamScope(null, "schema_a"), "admin-1", out _);
        using var second = Admit(manager, new ObservationStreamScope(string.Empty, "schema_b"), "admin-2", out _);

        Assert.Null(Admit(manager, new ObservationStreamScope(null, null), "admin-3", out var limit));
        Assert.Equal(ObservationStreamAdmissionLimit.Tenant, limit);
    }

    [UnitTest]
    public async Task TryCreateSession_ConcurrentCallers_NeverOverAdmitTheNode()
    {
        const int node = 50;
        const int attempts = 1_000;
        using var manager = CreateManager(perPrincipal: 1, perTenant: 1, node: node);
        using var start = new ManualResetEventSlim();
        var admitted = new System.Collections.Concurrent.ConcurrentBag<ObservationStreamSession>();
        var refusals = new int[4];

        var workers = Enumerable.Range(0, attempts).Select(i => Task.Run(() =>
        {
            start.Wait();
            // Every attempt is a distinct principal in a distinct tenant, so only the node cap binds.
            var session = Admit(manager, new ObservationStreamScope($"t{i}", "s"), $"p{i}", out var limit);
            if (session is null)
            {
                Interlocked.Increment(ref refusals[(int)limit]);
            }
            else
            {
                admitted.Add(session);
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(workers);

        Assert.Equal(node, admitted.Count);
        Assert.Equal(node, manager.SessionCount);
        Assert.Equal(attempts - node, refusals[(int)ObservationStreamAdmissionLimit.Node]);
        Assert.Equal(0, refusals[(int)ObservationStreamAdmissionLimit.Principal] + refusals[(int)ObservationStreamAdmissionLimit.Tenant]);

        foreach (var session in admitted)
        {
            session.Dispose();
        }

        Assert.Equal(0, manager.SessionCount);
    }

    [UnitTest]
    public void DefaultOptions_KeepEveryPerScopeCapBelowTheNodeCap()
    {
        var defaults = new ObservationStreamOptions();

        Assert.True(new ObservationStreamOptionsValidator().Validate(null, defaults).Succeeded);
        Assert.Equal(256, defaults.MaxConcurrentSessions);
        Assert.Equal(64, defaults.MaxSessionsPerTenant);
        Assert.Equal(8, defaults.MaxSessionsPerPrincipal);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(8, 256, 256, "MaxSessionsPerTenant must be less than MaxConcurrentSessions")]
    [InlineData(8, 300, 256, "MaxSessionsPerTenant must be less than MaxConcurrentSessions")]
    [InlineData(65, 64, 256, "MaxSessionsPerPrincipal must not exceed MaxSessionsPerTenant")]
    [InlineData(0, 64, 256, "MaxSessionsPerPrincipal must be a positive integer")]
    [InlineData(8, 64, -1, "MaxConcurrentSessions must be a positive integer")]
    public void Validator_CapThatRecreatesTheLockout_FailsStartup(int perPrincipal, int perTenant, int node, string expected)
    {
        var result = new ObservationStreamOptionsValidator().Validate(null, new ObservationStreamOptions
        {
            MaxSessionsPerPrincipal = perPrincipal,
            MaxSessionsPerTenant = perTenant,
            MaxConcurrentSessions = node
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure =>
            failure.StartsWith("SensorThings:Streaming:", StringComparison.Ordinal) && failure.Contains(expected, StringComparison.Ordinal));
    }
}
