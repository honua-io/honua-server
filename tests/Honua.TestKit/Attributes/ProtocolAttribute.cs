// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Xunit;

namespace Honua.TestKit.Attributes;

/// <summary>
/// Indicates which protocol(s) this test covers.
/// Used for test discovery and coverage analysis.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class ProtocolAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolAttribute"/> class.
    /// </summary>
    /// <param name="protocols">One or more protocol identifiers from <see cref="Constants.Protocols"/></param>
    public ProtocolAttribute(params string[] protocols)
    {
        foreach (var protocol in protocols)
        {
            Traits.Add(new TraitAttribute("Protocol", protocol));
        }
    }

    private List<TraitAttribute> Traits { get; } = [];
}
