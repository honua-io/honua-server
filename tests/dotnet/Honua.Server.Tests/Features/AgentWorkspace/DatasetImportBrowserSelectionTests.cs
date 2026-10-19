// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportBrowserSelectionTests
{
    private static BrowserImportFileSelection Selection => new("selection-original", "places.geojson", 123, new string('a', 64));

    [UnitTest]
    public void OriginalTurnRetainsSelectionAndRejectsSameTurnReplayWithReselectedFile()
    {
        var document = Document("human", "agent");
        var request = new CreateAgentTurn("turn-original", "view", 0, "Import my file", Selection);
        document = AgentWorkspaceState.AddTurn(document, "human", request);
        var original = document.Turns[request.TurnId];
        original.BrowserFileSelection.Should().Be(Selection);
        AgentWorkspaceState.AddTurn(document, "human", request).Should().BeSameAs(document);
        var changed = () => AgentWorkspaceState.AddTurn(document, "human", request with
        { BrowserFileSelection = Selection with { SelectionId = "selection-later" } });
        changed.Should().Throw<AgentWorkspaceConflictException>();
        static string Hash(AgentWorkspaceTurn turn) => Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
        Hash(original with { BrowserFileSelection = Selection with { TotalBytes = 124 } }).Should().NotBe(Hash(original));
        Hash(original with { BrowserFileSelection = Selection with { FileName = "other.geojson" } }).Should().NotBe(Hash(original));
        Hash(original with { BrowserFileSelection = Selection with { ExpectedSha256 = new string('b', 64) } }).Should().NotBe(Hash(original));
        Hash(original with { BrowserFileSelection = Selection with { SelectionId = "later" } }).Should().NotBe(Hash(original));
    }

    [UnitTheory]
    [InlineData("exact")]
    [InlineData("missing")]
    [InlineData("identity")]
    [InlineData("name")]
    [InlineData("bytes")]
    [InlineData("digest")]
    [InlineData("cross-turn")]
    [InlineData("foreign-executor")]
    public void BoundTaskSourceMustMatchOriginalHumanSelectionAndExecutor(string change)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "executor")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var document = Document("human", change == "foreign-executor" ? "other-agent" : actor);
        document = AgentWorkspaceState.AddTurn(document, "human", new("turn-original", "view", 0, "Import original file",
            change == "missing" ? null : Selection));
        document = AgentWorkspaceState.AddTurn(document, "human", new("turn-later", "view", 0, "Import later file",
            Selection with { SelectionId = "selection-later" }));
        var context = new DefaultHttpContext { User = principal };
        context.Request.Headers["X-Honua-Workspace"] = document.WorkspaceId;
        context.Request.Headers["X-Honua-Turn"] = "turn-original";
        var source = new DatasetImportSource("browser-file", change == "name" ? "later.geojson" : Selection.FileName,
            change == "bytes" ? 124 : Selection.TotalBytes, change == "digest" ? new string('b', 64) : Selection.ExpectedSha256,
            BrowserSelectionId: change is "identity" or "cross-turn" ? "selection-later" : Selection.SelectionId);
        var act = () => DatasetImportTaskService.RequireOriginalSource(context, document, source);
        if (change == "exact") act.Should().NotThrow();
        else act.Should().Throw<AgentWorkspaceConflictException>();
    }

    [UnitTheory]
    [InlineData("../places.geojson")]
    [InlineData("bucket/places.geojson")]
    [InlineData("C:\\places.geojson")]
    [InlineData("places.geojson\n")]
    public void BrowserFilenameRemainsInertBoundedMetadata(string filename)
    {
        var create = () => AgentWorkspaceState.AddTurn(Document("human", "agent"), "human",
            new("turn", "view", 0, "Import", Selection with { FileName = filename }));
        create.Should().Throw<ArgumentException>();
    }

    private static AgentWorkspaceDocument Document(string human, string agent)
    {
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace", TargetId = "target", OwnerId = human, AgentActorId = agent, Members = [human, agent] };
        document.Views["view"] = [AgentWorkspaceState.Current(document, "view")];
        return document;
    }
}
