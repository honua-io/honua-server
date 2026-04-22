// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a unit test (isolated, no external dependencies).
/// Unit tests run fastest and should comprise ~20% of test suite.
/// </summary>
[TraitAttribute("Category", "Unit")]
public sealed class UnitTestAttribute : FactAttribute
{
}
