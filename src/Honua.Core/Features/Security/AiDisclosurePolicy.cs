// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;

namespace Honua.Core.Features.Security;

/// <summary>Operator approval policy for a workload's model disclosure. This is not content classification or sanitization.</summary>
public sealed class AiDisclosurePolicyOptions
{
    /// <summary>Operator-asserted classification of every payload this workload may submit. Never populated from caller claims.</summary>
    public string WorkloadDataClassification { get; set; } = "unclassified";

    /// <summary>Explicit approvals. An empty list denies external transfers.</summary>
    public List<AiDisclosureApproval> Approvals { get; set; } = [];
}

/// <summary>An exact approval of one model disclosure boundary. Wildcards are not supported.</summary>
public sealed class AiDisclosureApproval
{
    /// <summary>Configured provider identity (Studio provider key, reporting provider or imagery provider).</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>Adapter kind (openai, anthropic, bedrock or http).</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Exact normalized HTTP base/invocation URI, or aws-bedrock:region for the AWS SDK.</summary>
    public string Destination { get; set; } = string.Empty;
    /// <summary>Exact effective model identifier, including any request override.</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>Server-selected use case: studio-chat, reporting-narrative or imagery-inference.</summary>
    public string UseCase { get; set; } = string.Empty;
    /// <summary>Operator-approved workload classification; unclassified requires explicit approval too.</summary>
    public string DataClassification { get; set; } = string.Empty;
}

/// <summary>Effective server-resolved transfer boundary, without prompt text, credentials, imagery or caller classification.</summary>
public sealed record AiDisclosureRequest(string Provider, string Kind, string Destination, string Model, string UseCase);

/// <summary>Pure, fail-closed approval evaluator shared by model transfer surfaces.</summary>
public static class AiDisclosurePolicy
{
    /// <summary>Whether a workload can send a payload to this exact provider/model/use-case destination.</summary>
    /// <remarks>The loopback exception requires callers to prohibit redirects. Private-network destinations are external.</remarks>
    public static bool IsAllowed(AiDisclosurePolicyOptions? options, AiDisclosureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options is null || string.IsNullOrWhiteSpace(options.WorkloadDataClassification)
            || string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Kind)
            || string.IsNullOrWhiteSpace(request.Model) || string.IsNullOrWhiteSpace(request.UseCase))
            return false;

        var destination = NormalizeDestination(request.Kind, request.Destination, out var loopback);
        if (destination is null)
            return false;
        if (loopback)
            return true;

        return options.Approvals?.Any(rule => rule is not null
            && string.Equals(rule.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rule.Kind, request.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeDestination(rule.Kind, rule.Destination, out _), destination, StringComparison.Ordinal)
            && string.Equals(rule.Model, request.Model, StringComparison.Ordinal)
            && string.Equals(rule.UseCase, request.UseCase, StringComparison.Ordinal)
            && string.Equals(rule.DataClassification, options.WorkloadDataClassification, StringComparison.Ordinal)) == true;
    }

    private static string? NormalizeDestination(string kind, string destination, out bool loopback)
    {
        loopback = false;
        if (string.IsNullOrWhiteSpace(destination))
            return null;
        if (string.Equals(kind, "bedrock", StringComparison.OrdinalIgnoreCase))
        {
            const string prefix = "aws-bedrock:";
            if (!destination.StartsWith(prefix, StringComparison.Ordinal))
                return null;
            var region = destination[prefix.Length..];
            return region.Length > 0 && region.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
                ? destination : null;
        }
        if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return null;

        // Only adapters with explicit no-redirect handlers can use the compatibility exception.
        var httpKind = string.Equals(kind, "openai", StringComparison.OrdinalIgnoreCase)
            || string.Equals(kind, "http", StringComparison.OrdinalIgnoreCase);
        var host = uri.Host.Trim('[', ']');
        loopback = httpKind && (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address)));
        if (!loopback && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        return uri.AbsoluteUri;
    }
}
