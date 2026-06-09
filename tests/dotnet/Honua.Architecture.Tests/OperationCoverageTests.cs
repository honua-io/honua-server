// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ensures every registered public-interface operation has at least one integration test.
/// Complements <see cref="ApiSurfaceCoverageTests"/> which covers HTTP endpoint-level coverage.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class OperationCoverageTests
{
    [ArchitectureTest]
    public void AllRegisteredOperations_HaveIntegrationTests()
    {
        var testedOperations = CollectIntegrationTestOperations();

        var missing = OperationRegistry.All
            .Where(op => !testedOperations.Contains(FormatKey(op.Protocol, op.Operation)))
            .Select(op => FormatKey(op.Protocol, op.Operation))
            .OrderBy(key => key)
            .ToArray();

        missing.Should().BeEmpty(
            "every registered operation in OperationRegistry must have at least one " +
            "integration test tagged with [InterfaceOperation]. " +
            "Add [InterfaceOperation(protocol, operation)] to an [IntegrationTest] method.");
    }

    private static HashSet<string> CollectIntegrationTestOperations()
    {
        // Server.Tests plus every extracted Honua.Protocols.<X>.Tests assembly:
        // e.g. the Mcp operation tests now live in Honua.Protocols.Mcp.Tests.
        var operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in ArchitectureTestHelpers.IntegrationTestMethods())
        {
            foreach (var attr in method.GetCustomAttributes(typeof(InterfaceOperationAttribute), inherit: true).Cast<InterfaceOperationAttribute>())
            {
                operations.Add(FormatKey(attr.Protocol, attr.Operation));
            }
        }

        return operations;
    }

    private static string FormatKey(string protocol, string operation) =>
        $"{protocol}::{operation}";
}
