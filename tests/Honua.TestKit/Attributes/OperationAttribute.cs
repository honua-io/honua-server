// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Indicates which operation(s) this test covers.
/// Used for test discovery and coverage analysis.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class OperationAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OperationAttribute"/> class.
    /// </summary>
    /// <param name="operations">One or more operation identifiers from <see cref="Constants.Operations"/></param>
    public OperationAttribute(params string[] operations)
    {
        foreach (var operation in operations)
        {
            Traits.Add(new TraitAttribute("Operation", operation));
        }
    }

    private List<TraitAttribute> Traits { get; } = [];
}
