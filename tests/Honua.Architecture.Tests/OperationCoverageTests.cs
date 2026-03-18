// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
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
        var testAssembly = typeof(Honua.Server.Tests.AdminEndpointTests).Assembly;
        var operations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in ArchitectureTestHelpers.GetTypesSafely(testAssembly))
        {
            var classHasIntegration = type.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                var methodHasIntegration = classHasIntegration ||
                    method.GetCustomAttributes(typeof(IntegrationTestAttribute), inherit: true).Length > 0;

                if (!methodHasIntegration)
                {
                    continue;
                }

                foreach (var attr in method.GetCustomAttributes(typeof(InterfaceOperationAttribute), inherit: true).Cast<InterfaceOperationAttribute>())
                {
                    operations.Add(FormatKey(attr.Protocol, attr.Operation));
                }
            }
        }

        return operations;
    }

    private static string FormatKey(string protocol, string operation) =>
        $"{protocol}::{operation}";
}
