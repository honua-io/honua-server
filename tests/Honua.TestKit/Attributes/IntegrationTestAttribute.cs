// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as an integration test (uses real database, HTTP, etc).
/// Integration tests should comprise ~70% of test suite.
/// </summary>
[TraitAttribute("Category", "Integration")]
public sealed class IntegrationTestAttribute : FactAttribute
{
}
