// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

public readonly record struct RequestSecretReferenceDecision(bool IsPermitted, string? Reason)
{
    public static RequestSecretReferenceDecision Permitted() => new(true, null);

    public static RequestSecretReferenceDecision Refused(string reason) => new(false, reason);
}

// A request-supplied reference must be a whole-string provider:identifier and must be allowed by policy; an empty policy permits nothing.
public interface IRequestSecretReferenceResolver
{
    // The decision does not depend on whether the named secret exists.
    RequestSecretReferenceDecision Evaluate(string? reference);

    Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default);
}

// The refusal message never contains the reference identifier or any resolved value.
public sealed class RequestSecretReferenceException : Exception
{
    public const string ClientSafeMessage = "The secret reference is not permitted or could not be resolved.";

    public RequestSecretReferenceException()
        : base(ClientSafeMessage)
    {
    }

    public RequestSecretReferenceException(string message)
        : base(message)
    {
    }

    public RequestSecretReferenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
