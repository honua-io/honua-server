// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Compliance;
using Honua.Core.Features.Compliance.Services;

namespace Honua.Core.Tests.Features.Compliance;

/// <summary>
/// Unit tests for <see cref="InMemoryEncryptionPostureProvider"/> — covers the
/// "encryption-at-rest keys can be rotated without downtime" acceptance criterion.
/// </summary>
public sealed class EncryptionPostureProviderTests
{
    [Fact]
    public void InitialPosture_ReportsVersionOne()
    {
        var provider = CreateProvider();
        var posture = provider.GetPosture();

        posture.ActiveKeyVersion.Should().Be(1);
        posture.RetainedKeyVersions.Should().Be(1);
        posture.LastRotationAt.Should().BeNull("no rotation has occurred yet");
        posture.Algorithms.Should().Contain("aes-256-gcm");
    }

    [Fact]
    public async Task Rotate_AppendsVersion_AndRetainsPrevious()
    {
        var provider = CreateProvider();

        var outcome = await provider.RotateAsync(requestedBy: "operator@honua.io", CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.PreviousVersion.Should().Be(1);
        outcome.NewVersion.Should().Be(2);
        outcome.Message.Should().Contain("existing ciphertext remains decryptable");

        var posture = provider.GetPosture();
        posture.ActiveKeyVersion.Should().Be(2);
        posture.RetainedKeyVersions.Should().Be(2, "previous version stays retained for decryption");
        posture.LastRotationAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Rotate_IsAuditLogged()
    {
        var audit = new CapturingAuditLog();
        var provider = CreateProvider(audit);

        await provider.RotateAsync("alice", CancellationToken.None);

        audit.Events.Should().HaveCount(1);
        var evt = audit.Events[0];
        evt.EventType.Should().Be(AuditEventType.ConfigChange);
        evt.Action.Should().Be("encryption.key.rotate");
        evt.Actor.Should().Be("alice");
        evt.ResourceType.Should().Be("encryption-key");
        evt.Outcome.Should().Be(AuditOutcome.Success);
    }

    [Fact]
    public async Task Rotate_TwiceQuickly_DoesNotDeadlockOrSkipVersions()
    {
        var provider = CreateProvider();

        var first = await provider.RotateAsync("op", CancellationToken.None);
        var second = await provider.RotateAsync("op", CancellationToken.None);

        first.NewVersion.Should().Be(2);
        second.PreviousVersion.Should().Be(2);
        second.NewVersion.Should().Be(3);

        var posture = provider.GetPosture();
        posture.ActiveKeyVersion.Should().Be(3);
        posture.RetainedKeyVersions.Should().Be(3);
    }

    [Fact]
    public async Task Rotate_Concurrent_AllVersionsUniqueAndContiguous()
    {
        var provider = CreateProvider();
        const int rotations = 32;

        var tasks = new Task<Honua.Core.Features.Compliance.Domain.KeyRotationOutcome>[rotations];
        for (var i = 0; i < rotations; i++)
        {
            tasks[i] = provider.RotateAsync($"op-{i}", CancellationToken.None);
        }

        var outcomes = await Task.WhenAll(tasks);

        outcomes.Should().AllSatisfy(o => o.Succeeded.Should().BeTrue());
        outcomes.Select(o => o.NewVersion).Should().OnlyHaveUniqueItems();
        outcomes.Select(o => o.NewVersion).Should().BeEquivalentTo(Enumerable.Range(2, rotations));

        var posture = provider.GetPosture();
        posture.ActiveKeyVersion.Should().Be(rotations + 1);
        posture.RetainedKeyVersions.Should().Be(rotations + 1);
    }

    [Fact]
    public void OperatorAttestedFipsMode_IsHonoured()
    {
        var opts = new ComplianceOptions
        {
            Encryption = new ComplianceEncryptionOptions
            {
                FipsModeAttested = true,
                Algorithms = new List<string> { "aes-256-gcm" },
            },
        };

        var provider = new InMemoryEncryptionPostureProvider(
            new TestOptionsMonitor<ComplianceOptions>(opts),
            NullAuditLog.Instance,
            TimeProvider.System);

        var posture = provider.GetPosture();
        posture.FipsMode.Should().BeTrue();
        posture.FipsSource.Should().Be("operator-attested");
    }

    private static InMemoryEncryptionPostureProvider CreateProvider(IAuditLog? audit = null) =>
        new(
            new TestOptionsMonitor<ComplianceOptions>(new ComplianceOptions()),
            audit ?? NullAuditLog.Instance,
            TimeProvider.System);
}

internal sealed class CapturingAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = new();

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
