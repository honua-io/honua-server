// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Editing;

/// <summary>
/// Proves the shared edit-pipeline decision for owner-based edit policies (SEC-5): a write that
/// reaches <see cref="IFeatureWriter"/> through any protocol is held to the resource's owner
/// policy, and a refused batch never reaches the provider writer.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class OwnerEditPolicyEnforcingFeatureWriterTests
{
    private const int StorageLayerId = 7;
    private const string OwnerField = "created_by";

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_UpdateOfAnotherPrincipalsRow_RollsBackWithoutCallingTheProvider()
    {
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))]));

        result.WasRolledBack.Should().BeTrue();
        result.UpdateResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(OwnerEditPolicyEnforcingFeatureWriter.NotPermittedErrorCode);
        inner.ApplyEditsCalls.Should().Be(0, "a refused batch must never reach the provider");
    }

    [UnitTest]
    [Operation(Operations.Delete)]
    public async Task ApplyEditsAsync_DeleteOfAnotherPrincipalsRow_RollsBackWithoutCallingTheProvider()
    {
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(StorageLayerId, FeatureEditBatch.Create(deletes: [42L]));

        result.WasRolledBack.Should().BeTrue();
        result.DeleteResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(OwnerEditPolicyEnforcingFeatureWriter.NotPermittedErrorCode);
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_OrderedChangeSetTouchingAnotherPrincipalsRow_RollsBackTheWholeBatch()
    {
        // The ordered-operations shape is what the OData change set and the OGC batch build.
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            new FeatureEditBatch
            {
                Operations =
                [
                    FeatureEditOperation.Create(Row(0, ("name", "new"))),
                    FeatureEditOperation.Update(Row(42, ("name", "changed")))
                ]
            });

        result.WasRolledBack.Should().BeTrue();
        result.CreateResults.Should().ContainSingle().Which.IsSuccess.Should().BeFalse();
        result.UpdateResults.Should().ContainSingle()
            .Which.ErrorCode.Should().Be(OwnerEditPolicyEnforcingFeatureWriter.NotPermittedErrorCode);
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task TransactionApplyEditsAsync_UpdateOfAnotherPrincipalsRow_RollsBackWithoutCallingTheProvider()
    {
        // OData atomic change sets, the OGC batch and multi-layer WFS-T write through a transaction.
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        await using var transaction = await writer.BeginTransactionAsync();
        var result = await transaction.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))]));

        result.WasRolledBack.Should().BeTrue();
        inner.Transaction.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_OwnerUpdatesOwnRow_PassesThrough()
    {
        var (writer, inner) = CreateWriter(User("alice"), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))], deletes: [42L]));

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_AdministratorUpdatesAnotherPrincipalsRow_PassesThrough()
    {
        var (writer, inner) = CreateWriter(User("root", "admin"), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))]));

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ApplyEditsAsync_AnonymousCreate_RollsBackWithoutCallingTheProvider()
    {
        var (writer, inner) = CreateWriter(new ClaimsPrincipal(new ClaimsIdentity()), rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(creates: [Row(0, ("name", "new"))]));

        result.WasRolledBack.Should().BeTrue();
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    public async Task ApplyEditsAsync_Create_StampsTheCreatingPrincipalAsOwner()
    {
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(creates: [Row(0, ("name", "new"), (OwnerField, "alice"))]));

        inner.ApplyEditsCalls.Should().Be(1);
        inner.LastBatch!.Value.Creates.Should().ContainSingle()
            .Which.Attributes[OwnerField].Should().Be("bob", "the owner is the creator, not a request value");
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_WithNoRequestOrJobPrincipal_RefusesTheWrite()
    {
        var (writer, inner) = CreateWriter(user: null, rowOwner: "alice");

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))]));

        result.WasRolledBack.Should().BeTrue();
        inner.ApplyEditsCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task UpdateAsync_AnotherPrincipalsRow_ThrowsWithoutCallingTheProvider()
    {
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice");

        var act = () => writer.UpdateAsync(StorageLayerId, Row(42, ("name", "changed")));

        await act.Should().ThrowAsync<FeatureEditNotPermittedException>();
        inner.UpdateCalls.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_OwnerUpdate_CarriesTheOwnerReadAsAPrecondition()
    {
        // The writer re-checks this snapshot inside its transaction, so an ownership change
        // between the owner read and the write fails the write instead of passing it.
        var (writer, inner) = CreateWriter(User("alice"), rowOwner: "alice");

        await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))]));

        var expectedToken = FeatureStateToken.FromReadSnapshot(Row(42, (OwnerField, "alice")));
        inner.LastBatch!.Value.Preconditions.Should().ContainSingle(precondition =>
            precondition.ObjectId == 42 && precondition.ExpectedStateToken == expectedToken);
    }

    [UnitTheory]
    [InlineData("alice", true)]
    [InlineData("bob", false)]
    [Operation(Operations.Delete)]
    public async Task ApplyEditsAsync_InABackgroundJob_EvaluatesTheCapturedSubmitter(string submitter, bool written)
    {
        var (writer, inner) = CreateWriter(user: null, rowOwner: "alice");

        FeatureEditResult result;
        using (JobSecurityScope.Begin(new JobSecurityContext(
                   submitter,
                   TenantId: null,
                   Claims: [new JobSecurityClaim(ClaimTypes.Name, submitter)])))
        {
            result = await writer.ApplyEditsAsync(StorageLayerId, FeatureEditBatch.Create(deletes: [42L]));
        }

        result.WasRolledBack.Should().Be(!written);
        inner.ApplyEditsCalls.Should().Be(written ? 1 : 0);
    }

    [UnitTest]
    [Operation(Operations.Update)]
    public async Task ApplyEditsAsync_LayerWithoutOwnerPolicy_PassesThroughWithoutReadingRows()
    {
        var reader = Substitute.For<IFeatureReader>();
        var (writer, inner) = CreateWriter(User("bob"), rowOwner: "alice", ownerPolicyEnabled: false, reader: reader);

        var result = await writer.ApplyEditsAsync(
            StorageLayerId,
            FeatureEditBatch.Create(updates: [Row(42, ("name", "changed"))], deletes: [42L]));

        result.WasRolledBack.Should().BeFalse();
        inner.ApplyEditsCalls.Should().Be(1);
        await reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default);
    }

    private static (OwnerEditPolicyEnforcingFeatureWriter Writer, RecordingFeatureWriter Inner) CreateWriter(
        ClaimsPrincipal? user,
        string rowOwner,
        bool ownerPolicyEnabled = true,
        IFeatureReader? reader = null)
    {
        if (reader is null)
        {
            reader = Substitute.For<IFeatureReader>();
            reader.GetAsync(StorageLayerId, Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<Feature?>(Row(call.ArgAt<long>(1), (OwnerField, rowOwner))));
        }

        var services = new ServiceCollection();
        services.AddSingleton(reader);
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        var provider = services.BuildServiceProvider();

        var accessor = new HttpContextAccessor();
        if (user is not null)
        {
            accessor.HttpContext = new DefaultHttpContext { RequestServices = provider, User = user };
        }

        var inner = new RecordingFeatureWriter();
        var writer = new OwnerEditPolicyEnforcingFeatureWriter(
            inner,
            BuildGraph(ownerPolicyEnabled),
            accessor,
            provider);
        return (writer, inner);
    }

    private static TestMetadataV2GraphProvider BuildGraph(bool ownerPolicyEnabled)
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-inspections",
                "inspections",
                fields:
                [
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                    new MetadataV2Field { Name = OwnerField, Type = MetadataV2FieldType.String }
                ])
            .AddStorageBinding("binding-inspections", "res-inspections", "public.inspections", storageLayerId: StorageLayerId)
            .AddService("svc-inspections", "inspections", protocols: ["OData", "Grpc"])
            .AddPublication("pub-inspections", "svc-inspections", "res-inspections", layerIndex: 0, storageBindingId: "binding-inspections")
            .Build();

        var resources = graph.Resources
            .Select(resource => resource with
            {
                OwnerEditPolicy = new MetadataV2OwnerEditPolicy { Enabled = ownerPolicyEnabled, OwnerField = OwnerField }
            })
            .ToArray();
        return new TestMetadataV2GraphProvider(graph with { Resources = resources });
    }

    private static ClaimsPrincipal User(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static Feature Row(long id, params (string Name, object? Value)[] attributes)
        => Feature.Create(
            id,
            geometry: null,
            attributes.ToImmutableDictionary(
                static attribute => attribute.Name,
                static attribute => attribute.Value,
                StringComparer.OrdinalIgnoreCase));

    private sealed class RecordingFeatureWriter : IFeatureWriter
    {
        public int ApplyEditsCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public FeatureEditBatch? LastBatch { get; private set; }

        public RecordingTransaction Transaction { get; } = new();

        public Task<IFeatureWriterTransaction> BeginTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IFeatureWriterTransaction>(Transaction);

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => Task.FromResult(feature);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            return Task.FromResult(feature);
        }

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            ApplyEditsCalls++;
            LastBatch = editBatch;
            return Task.FromResult(FeatureEditResult.Success(
                editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length,
                editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length,
                editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length));
        }
    }

    private sealed class RecordingTransaction : IFeatureWriterTransaction
    {
        public int ApplyEditsCalls { get; private set; }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            ApplyEditsCalls++;
            return Task.FromResult(FeatureEditResult.Success(0, 0, 0));
        }

        public Task<FeatureWriterTransactionCommitOutcome> CommitAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FeatureWriterTransactionCommitOutcome.Committed);

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
