// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Tests.Features.Geoprocessing;

/// <summary>
/// Tests for workspace and artifact domain model behavior.
/// </summary>
public class WorkspaceLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Workspace_IsExpired_ReturnsTrueAfterExpiresAt()
    {
        var workspace = CreateWorkspace(expiresAt: Now.AddHours(-1));

        Assert.True(workspace.IsExpired(Now));
    }

    [Fact]
    public void Workspace_IsExpired_ReturnsFalseBeforeExpiresAt()
    {
        var workspace = CreateWorkspace(expiresAt: Now.AddHours(1));

        Assert.False(workspace.IsExpired(Now));
    }

    [Fact]
    public void Workspace_IsExpired_ReturnsTrueAtExactExpiration()
    {
        var workspace = CreateWorkspace(expiresAt: Now);

        Assert.True(workspace.IsExpired(Now));
    }

    [Fact]
    public void Workspace_IsExpired_ReturnsFalseWhenNoExpiration()
    {
        var workspace = CreateWorkspace(expiresAt: null);

        Assert.False(workspace.IsExpired(Now));
    }

    [Fact]
    public void Workspace_ToRef_PreservesFields()
    {
        var workspace = CreateWorkspace(
            kind: WorkspaceKind.Scratch,
            label: "test-ws",
            uri: "file:///tmp/ws",
            expiresAt: Now.AddHours(1));

        var @ref = workspace.ToRef();

        Assert.Equal(workspace.WorkspaceId, @ref.WorkspaceId);
        Assert.Equal(workspace.Kind, @ref.Kind);
        Assert.Equal(workspace.Label, @ref.Label);
        Assert.Equal(workspace.Uri, @ref.Uri);
        Assert.Equal(workspace.ExpiresAt, @ref.ExpiresAt);
    }

    [Fact]
    public void Artifact_ToRef_PreservesFields()
    {
        var metadata = new Dictionary<string, string> { ["key"] = "value" };
        var artifact = new Artifact
        {
            ArtifactId = "art-1",
            Kind = ArtifactKind.FeatureLayer,
            Label = "result-layer",
            State = ArtifactLifecycleState.Available,
            Uri = "pg://table/result",
            ContentType = "application/geo+json",
            SizeBytes = 1024,
            CreatedAt = Now,
            Metadata = metadata,
            WorkspaceId = "ws-1"
        };

        var @ref = artifact.ToRef();

        Assert.Equal(artifact.ArtifactId, @ref.ArtifactId);
        Assert.Equal(artifact.Kind, @ref.Kind);
        Assert.Equal(artifact.Label, @ref.Label);
        Assert.Equal(artifact.Uri, @ref.Uri);
        Assert.Equal(artifact.ContentType, @ref.ContentType);
        Assert.Equal("value", @ref.Metadata["key"]);
    }

    [Fact]
    public void ArtifactPromotionResult_Success_HasPromotedId()
    {
        var result = ArtifactPromotionResult.Success("promoted-123");

        Assert.True(result.Succeeded);
        Assert.Equal("promoted-123", result.PromotedArtifactId);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void ArtifactPromotionResult_Failure_HasReason()
    {
        var result = ArtifactPromotionResult.Failure("not eligible");

        Assert.False(result.Succeeded);
        Assert.Null(result.PromotedArtifactId);
        Assert.Equal("not eligible", result.FailureReason);
    }

    [Fact]
    public void CleanupResult_None_IsEmpty()
    {
        var result = CleanupResult.None;

        Assert.Equal(0, result.WorkspacesExpired);
        Assert.Equal(0, result.WorkspacesDeleted);
        Assert.Equal(0, result.ArtifactsDeleted);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Empty(result.Errors);
    }

    private static Workspace CreateWorkspace(
        WorkspaceKind kind = WorkspaceKind.Scratch,
        string label = "test",
        string? uri = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        WorkspaceId = Guid.NewGuid().ToString("N"),
        Kind = kind,
        Label = label,
        OwnerId = "owner-1",
        State = WorkspaceLifecycleState.Active,
        Uri = uri,
        CreatedAt = Now.AddHours(-2),
        ExpiresAt = expiresAt
    };
}
