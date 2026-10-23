// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Outcome of evaluating a request-supplied secret reference against the operator policy.
/// </summary>
/// <param name="IsPermitted">Whether the reference may be resolved.</param>
/// <param name="Reason">A client-safe explanation when <paramref name="IsPermitted"/> is <see langword="false"/>.</param>
public readonly record struct RequestSecretReferenceDecision(bool IsPermitted, string? Reason)
{
    /// <summary>A decision that permits the reference.</summary>
    public static RequestSecretReferenceDecision Permitted() => new(true, null);

    /// <summary>A decision that refuses the reference with a client-safe reason.</summary>
    public static RequestSecretReferenceDecision Refused(string reason) => new(false, reason);
}

/// <summary>
/// Resolves secret references that were supplied by a request (or persisted from one), as opposed
/// to references the server reads from its own configuration. Every reference must use the
/// whole-string <c>provider:identifier</c> grammar and be permitted by
/// <see cref="RequestSecretReferenceOptions"/>; nothing is permitted when the policy is empty.
/// </summary>
/// <remarks>
/// Request-handling code must depend on this contract and never on
/// <see cref="IConnectionSecretResolver"/> or <see cref="ISecretProvider"/>, which resolve
/// configuration-supplied references without a policy.
/// </remarks>
public interface IRequestSecretReferenceResolver
{
    /// <summary>
    /// Evaluates a reference without resolving it. The decision does not depend on whether the
    /// named secret exists.
    /// </summary>
    RequestSecretReferenceDecision Evaluate(string? reference);

    /// <summary>
    /// Resolves a permitted reference to its value.
    /// </summary>
    /// <exception cref="RequestSecretReferenceException">
    /// The reference is malformed, not permitted, or could not be resolved. The message is
    /// client-safe and identical for all three cases.
    /// </exception>
    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when a request-supplied secret reference is refused or cannot be resolved. The message
/// never contains the reference identifier or any resolved value.
/// </summary>
public sealed class RequestSecretReferenceException : Exception
{
    /// <summary>The single client-safe message used for every refusal or resolution failure.</summary>
    public const string ClientSafeMessage = "The secret reference is not permitted or could not be resolved.";

    /// <summary>Creates the exception with the client-safe message.</summary>
    public RequestSecretReferenceException()
        : base(ClientSafeMessage)
    {
    }

    /// <summary>Creates the exception with a caller-supplied message.</summary>
    public RequestSecretReferenceException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a caller-supplied message and inner exception.</summary>
    public RequestSecretReferenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
