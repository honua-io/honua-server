// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Unit tests for the MCP Streamable-HTTP session registry (honua-server#1954).
/// These run without Docker/PostGIS so the session-id issuance and validation
/// invariants are covered even when the full integration harness is unavailable.
/// </summary>
public sealed class McpSessionManagerTests
{
    [UnitTest]
    public void CreateSession_IssuesUniqueVisibleAsciiIds()
    {
        var manager = new McpSessionManager();

        var ids = Enumerable.Range(0, 100).Select(_ => manager.CreateSession()).ToArray();

        ids.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id));
        ids.Distinct().Should().HaveCount(ids.Length, "session ids must be globally unique");
        // The spec requires session ids to contain only visible ASCII (0x21-0x7E).
        ids.Should().OnlyContain(id => id.All(c => c >= '!' && c <= '~'));
    }

    [UnitTest]
    public void IsValid_ReturnsTrueForIssuedSession_AndFalseOtherwise()
    {
        var manager = new McpSessionManager();
        var id = manager.CreateSession();

        manager.IsValid(id).Should().BeTrue();
        manager.IsValid("never-issued").Should().BeFalse();
        manager.IsValid(string.Empty).Should().BeFalse();
        manager.IsValid(null!).Should().BeFalse();
    }

    [UnitTest]
    public void Terminate_RemovesSession_AndIsIdempotent()
    {
        var manager = new McpSessionManager();
        var id = manager.CreateSession();

        manager.Terminate(id).Should().BeTrue();
        manager.IsValid(id).Should().BeFalse();
        // A second termination of the same id reports no active session removed.
        manager.Terminate(id).Should().BeFalse();
        manager.Terminate("never-issued").Should().BeFalse();
    }
}
