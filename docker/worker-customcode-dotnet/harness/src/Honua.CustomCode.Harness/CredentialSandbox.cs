// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Harness;

/// <summary>Environment hygiene and scoped client construction.</summary>
/// <remarks>
/// Removing environment entries is not a hostile-code security boundary. Same-process code
/// can inspect its intended job capability; deployment must provide no cloud task credentials.
/// The server enforces that capability's frozen scope and current running job attempt.
/// </remarks>
public static class CredentialSandbox
{
    /// <summary>
    /// Env vars that hand out ambient cloud credentials or the scoped token. These are
    /// deleted after the scoped client is built and BEFORE user code is activated.
    /// </summary>
    public static readonly IReadOnlySet<string> StrippedEnvVars = new HashSet<string>(StringComparer.Ordinal)
    {
        "HONUA_JOB_TOKEN",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN",
        "AWS_CONTAINER_AUTHORIZATION_TOKEN_FILE",
        "ECS_CONTAINER_METADATA_URI",
        "ECS_CONTAINER_METADATA_URI_V4",
        "ECS_CONTAINER_METADATA_FILE",
        // Static keys, if ever injected, must not leak to user code either.
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_WEB_IDENTITY_TOKEN_FILE",
    };

    /// <summary>
    /// Delete ambient-credential + token env vars in place. Returns the names actually
    /// removed (useful for logging/asserting). Operates on the process environment by
    /// default; an injected <paramref name="env"/> is used for tests.
    /// </summary>
    /// <param name="env">An optional in-memory environment (for tests).</param>
    /// <returns>The sorted names that were removed.</returns>
    public static IReadOnlyList<string> StripCredentialEnv(IDictionary<string, string?>? env = null)
    {
        var removed = new List<string>();
        foreach (var name in StrippedEnvVars)
        {
            if (env is null)
            {
                if (Environment.GetEnvironmentVariable(name) is not null)
                {
                    Environment.SetEnvironmentVariable(name, null);
                    removed.Add(name);
                }
            }
            else if (env.Remove(name))
            {
                removed.Add(name);
            }
        }

        removed.Sort(StringComparer.Ordinal);
        return removed;
    }

    /// <summary>
    /// Throw if any stripped var is still present (post-scrub invariant check).
    /// </summary>
    /// <param name="env">An optional in-memory environment (for tests).</param>
    /// <exception cref="InvalidOperationException">When a credential var still remains.</exception>
    public static void AssertCredentialsStripped(IDictionary<string, string?>? env = null)
    {
        var leaked = StrippedEnvVars
            .Where(name => env is null
                ? Environment.GetEnvironmentVariable(name) is not null
                : env.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (leaked.Count > 0)
        {
            throw new InvalidOperationException(
                $"credential env not fully stripped: {string.Join(", ", leaked)}");
        }
    }

    /// <summary>
    /// Construct the scoped Honua client from the job-bound bearer token. The default
    /// factory wires the real Honua SDK (<c>AddHonua(o =&gt; o.BaseAddress = ...;
    /// o.BearerTokenProvider = _ =&gt; Task.FromResult(token))</c>); tests inject a fake.
    /// </summary>
    /// <param name="baseUrl">The Honua API base URL.</param>
    /// <param name="jobToken">The scoped, job-bound bearer token.</param>
    /// <param name="clientFactory">An injectable factory (for tests). Defaults to the real SDK wiring.</param>
    /// <param name="jobId">Required job context for the production SDK factory.</param>
    /// <returns>The constructed scoped client (opaque to the harness).</returns>
    public static object BuildScopedClient(
        string baseUrl,
        string jobToken,
        Func<string, string, object>? clientFactory = null,
        string? jobId = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("baseUrl is required to build the scoped client.", nameof(baseUrl));
        }

        if (string.IsNullOrWhiteSpace(jobToken))
        {
            throw new ArgumentException("jobToken is required to build the scoped client.", nameof(jobToken));
        }

        return clientFactory is not null
            ? clientFactory(baseUrl, jobToken)
            : ScopedHonuaClientFactory.Create(baseUrl, jobToken, jobId ?? throw new ArgumentException("jobId is required for the production SDK client.", nameof(jobId)));
    }
}
