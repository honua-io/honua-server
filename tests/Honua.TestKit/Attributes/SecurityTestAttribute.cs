// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Attributes;

/// <summary>
/// Marks a test as a security-focused test for categorization and reporting.
/// </summary>
/// <remarks>
/// Security tests should cover:
/// - Encryption and cryptographic operations
/// - Authentication and authorization
/// - Data sanitization and security boundaries
/// - Input validation and security controls
/// - Audit logging and security monitoring
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SecurityTestAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the SecurityTestAttribute.
    /// </summary>
    public SecurityTestAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the SecurityTestAttribute with a description.
    /// </summary>
    /// <param name="description">Description of the security aspect being tested</param>
    public SecurityTestAttribute(string description)
    {
        Description = description;
    }

    /// <summary>
    /// Description of the security aspect being tested.
    /// </summary>
    public string? Description { get; }
}
