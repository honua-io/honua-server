// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Kind of Kubernetes object an environment source points at.
/// </summary>
internal enum KubernetesEnvSourceKind
{
    /// <summary>A core/v1 <c>Secret</c>.</summary>
    Secret,

    /// <summary>A core/v1 <c>ConfigMap</c>.</summary>
    ConfigMap
}

/// <summary>
/// One container <c>envFrom</c> entry: every key of a Secret or ConfigMap becomes an environment
/// variable, optionally prefixed. Only the object's name is known to the server; its contents are
/// resolved by the kubelet when the pod starts.
/// </summary>
/// <param name="Kind">Secret or ConfigMap.</param>
/// <param name="Name">The Secret/ConfigMap name in the Job's namespace.</param>
/// <param name="Prefix">Optional prefix prepended to every imported key.</param>
internal sealed record KubernetesEnvFromSource(KubernetesEnvSourceKind Kind, string Name, string? Prefix = null);

/// <summary>
/// A single environment variable sourced from one key of a Secret (<c>secretKeyRef</c>) or
/// ConfigMap (<c>configMapKeyRef</c>). Only the reference is known to the server.
/// </summary>
/// <param name="Kind">Secret or ConfigMap.</param>
/// <param name="Name">The Secret/ConfigMap name in the Job's namespace.</param>
/// <param name="Key">The data key inside that object.</param>
internal sealed record KubernetesEnvKeyReference(KubernetesEnvSourceKind Kind, string Name, string Key);

/// <summary>
/// Why a Kubernetes environment-source parameter was refused.
/// </summary>
internal enum KubernetesJobParameterError
{
    /// <summary>A <c>k8s.envFrom.*</c> key is not <c>k8s.envFrom.secret.&lt;n&gt;</c> or <c>k8s.envFrom.configMap.&lt;n&gt;</c>.</summary>
    InvalidEnvFromKey,

    /// <summary>The referenced Secret/ConfigMap name is not a valid DNS-1123 subdomain.</summary>
    InvalidObjectName,

    /// <summary>An <c>envFrom</c> prefix is not a valid environment variable name fragment.</summary>
    InvalidEnvFromPrefix,

    /// <summary>A key reference is not of the shape <c>&lt;name&gt;:&lt;key&gt;</c> with a valid data key.</summary>
    InvalidKeyReference,

    /// <summary>The environment variable name is empty or not a valid Kubernetes environment variable name.</summary>
    InvalidEnvironmentVariableName,

    /// <summary>The key-reference suffix is misspelled (only <c>secretKeyRef</c> and <c>configMapKeyRef</c> exist).</summary>
    UnknownValueSource,

    /// <summary>The variable is a reserved execution-worker launch variable.</summary>
    ReservedEnvironmentVariable,

    /// <summary>The same variable is defined both literally and by reference, or by two references.</summary>
    ConflictingEnvironmentVariable
}

/// <summary>
/// Raised when a workload/job parameter that wires Kubernetes Secrets or ConfigMaps into a worker
/// pod is malformed. The message names the offending parameter key and the reason, never a literal
/// parameter value.
/// </summary>
internal sealed class KubernetesJobParameterException(
    string parameterKey,
    KubernetesJobParameterError error,
    string message) : Exception(message)
{
    /// <summary>The offending parameter (or configuration) key.</summary>
    public string ParameterKey { get; } = parameterKey;

    /// <summary>The typed refusal reason.</summary>
    public KubernetesJobParameterError Error { get; } = error;
}

/// <summary>
/// The container environment the Kubernetes Job backend renders for one worker pod.
/// </summary>
/// <param name="Literals">Literal <c>env[].value</c> entries (workload passthrough plus launch variables).</param>
/// <param name="References">
/// <c>env[].valueFrom</c> entries by variable name. Only Secret/ConfigMap names and keys.
/// </param>
/// <param name="EnvFrom">Container <c>envFrom</c> entries, in precedence order (later wins).</param>
internal sealed record KubernetesJobEnvironment(
    IReadOnlyDictionary<string, string> Literals,
    IReadOnlyDictionary<string, KubernetesEnvKeyReference> References,
    IReadOnlyList<KubernetesEnvFromSource> EnvFrom);

/// <summary>
/// Builds and validates the worker container environment for the Kubernetes Job backend.
/// </summary>
/// <remarks>
/// <para>
/// Three sources are merged: literal <c>env.*</c> / <c>k8s.env.*</c> passthrough, single-variable
/// Secret/ConfigMap references (<c>k8s.env.&lt;NAME&gt;.secretKeyRef=&lt;secret&gt;:&lt;key&gt;</c>,
/// <c>k8s.env.&lt;NAME&gt;.configMapKeyRef=&lt;configMap&gt;:&lt;key&gt;</c>), and whole-object
/// imports (<c>k8s.envFrom.secret.&lt;n&gt;=&lt;secret&gt;[:&lt;prefix&gt;]</c>,
/// <c>k8s.envFrom.configMap.&lt;n&gt;=&lt;configMap&gt;[:&lt;prefix&gt;]</c>) plus the configured
/// <c>ControlPlane:Kubernetes:DefaultEnvFromSecrets</c>/<c>DefaultEnvFromConfigMaps</c>.
/// </para>
/// <para>
/// The server only ever handles reference names: Secret contents are resolved by the kubelet, so they
/// never reach the Job object, the durable job record or the server's logs. The execution-worker
/// launch variables (<see cref="ExecutionWorkerLaunchEnvironment"/>) stay literal, are stamped last,
/// cannot be targeted by a reference, and — because Kubernetes gives explicit <c>env</c> precedence over
/// <c>envFrom</c> — cannot be shadowed by an imported Secret either.
/// </para>
/// </remarks>
internal static class KubernetesJobEnvironmentBuilder
{
    /// <summary>
    /// Builds the environment for <paramref name="job"/>.
    /// </summary>
    /// <exception cref="KubernetesJobParameterException">A reference parameter or default is malformed.</exception>
    public static KubernetesJobEnvironment Build(
        ExecutionJobRecord job,
        IReadOnlyDictionary<string, string> parameters,
        int executionAttempt,
        KubernetesExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<KubernetesJobParameterException>();
        var envFrom = ResolveDefaultEnvFrom(options, failures);
        var references = new Dictionary<string, KubernetesEnvKeyReference>(StringComparer.Ordinal);
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        ResolveParameters(parameters, literals, references, envFrom, failures);
        if (failures.Count > 0)
        {
            throw failures[0];
        }

        // The launch contract shared by every batch backend (HONUA_OPERATION_ID, kind, attempt, and the
        // serving-to-worker contract version of ADR-0060 #3b) is stamped AFTER the passthrough, so a
        // workload can never shadow it. A generic server image reads it to start as a single-job
        // execution worker.
        foreach (var variable in ExecutionWorkerLaunchEnvironment.Build(job, executionAttempt))
        {
            literals[variable.Key] = variable.Value;
        }

        return new KubernetesJobEnvironment(literals, references, envFrom);
    }

    /// <summary>
    /// Validates workload parameters without building an environment (used at startup).
    /// </summary>
    public static IReadOnlyList<KubernetesJobParameterException> ValidateParameters(
        IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var failures = new List<KubernetesJobParameterException>();
        ResolveParameters(
            parameters,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, KubernetesEnvKeyReference>(StringComparer.Ordinal),
            [],
            failures);
        return failures;
    }

    /// <summary>
    /// Validates the configured default <c>envFrom</c> sources (used at startup).
    /// </summary>
    public static IReadOnlyList<KubernetesJobParameterException> ValidateDefaults(KubernetesExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<KubernetesJobParameterException>();
        ResolveDefaultEnvFrom(options, failures);
        return failures;
    }

    private static List<KubernetesEnvFromSource> ResolveDefaultEnvFrom(
        KubernetesExecutionOptions options,
        List<KubernetesJobParameterException> failures)
    {
        var sources = new List<KubernetesEnvFromSource>();
        AddDefaults(options.DefaultEnvFromSecrets, KubernetesEnvSourceKind.Secret, "DefaultEnvFromSecrets");
        AddDefaults(options.DefaultEnvFromConfigMaps, KubernetesEnvSourceKind.ConfigMap, "DefaultEnvFromConfigMaps");
        return sources;

        void AddDefaults(List<string>? entries, KubernetesEnvSourceKind kind, string property)
        {
            if (entries is null)
            {
                return;
            }

            for (var i = 0; i < entries.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(entries[i]))
                {
                    continue;
                }

                var key = $"ControlPlane:Kubernetes:{property}:{i.ToString(CultureInfo.InvariantCulture)}";
                var source = ParseEnvFromValue(key, kind, entries[i], failures);
                if (source is not null && !sources.Contains(source))
                {
                    sources.Add(source);
                }
            }
        }
    }

    private static void ResolveParameters(
        IReadOnlyDictionary<string, string> parameters,
        Dictionary<string, string> literals,
        Dictionary<string, KubernetesEnvKeyReference> references,
        List<KubernetesEnvFromSource> envFrom,
        List<KubernetesJobParameterException> failures)
    {
        // Literal passthrough: generic env.* first, then the Kubernetes-specific k8s.env.* (which wins).
        // Reserved launch variables are skipped, matching the AWS Batch adapter: a workload may never
        // set them, even when the launch would not stamp them for this job.
        var literalKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prefix in new[]
                 {
                     KubernetesJobParameterKeys.GenericEnvironmentPrefix,
                     KubernetesJobParameterKeys.EnvironmentPrefix
                 })
        {
            foreach (var (key, value) in parameters)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || TryGetValueSourceSuffix(key, out _, out _))
                {
                    continue;
                }

                var name = key[prefix.Length..];
                if (string.IsNullOrWhiteSpace(name) || ExecutionWorkerLaunchEnvironment.IsReserved(name))
                {
                    continue;
                }

                literals[name] = value;
                literalKeys[name] = key;
            }
        }

        var referenceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var workloadEnvFrom = new List<(string Ordinal, KubernetesEnvSourceKind Kind, KubernetesEnvFromSource Source)>();
        foreach (var (key, value) in parameters.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (key.StartsWith(KubernetesJobParameterKeys.EnvFromPrefix, StringComparison.Ordinal))
            {
                ResolveEnvFromParameter(key, value, workloadEnvFrom, failures);
                continue;
            }

            if (!key.StartsWith(KubernetesJobParameterKeys.EnvironmentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (TryGetValueSourceSuffix(key, out var suffix, out var exactSpelling))
            {
                if (!exactSpelling)
                {
                    failures.Add(new(
                        key,
                        KubernetesJobParameterError.UnknownValueSource,
                        $"Kubernetes parameter '{key}' uses an unknown value source; use "
                        + $"'{KubernetesJobParameterKeys.SecretKeyRefSuffix}' or "
                        + $"'{KubernetesJobParameterKeys.ConfigMapKeyRefSuffix}' (case-sensitive)."));
                    continue;
                }

                var kind = string.Equals(suffix, KubernetesJobParameterKeys.SecretKeyRefSuffix, StringComparison.Ordinal)
                    ? KubernetesEnvSourceKind.Secret
                    : KubernetesEnvSourceKind.ConfigMap;
                var name = key[KubernetesJobParameterKeys.EnvironmentPrefix.Length..^suffix.Length];
                ResolveKeyReference(key, name, kind, value, literalKeys, referenceKeys, references, failures);
            }
        }

        // A re-declared default keeps its first (lower-precedence) position; duplicates add nothing.
        var ordered = workloadEnvFrom
            .OrderBy(entry => entry.Ordinal, EnvFromOrdinalComparer.Instance)
            .ThenBy(entry => entry.Kind)
            .Select(entry => entry.Source)
            .Distinct()
            .Where(source => !envFrom.Contains(source))
            .ToList();
        envFrom.AddRange(ordered);
    }

    private static void ResolveEnvFromParameter(
        string key,
        string value,
        List<(string Ordinal, KubernetesEnvSourceKind Kind, KubernetesEnvFromSource Source)> envFrom,
        List<KubernetesJobParameterException> failures)
    {
        KubernetesEnvSourceKind kind;
        string ordinal;
        if (key.StartsWith(KubernetesJobParameterKeys.EnvFromSecretPrefix, StringComparison.Ordinal))
        {
            kind = KubernetesEnvSourceKind.Secret;
            ordinal = key[KubernetesJobParameterKeys.EnvFromSecretPrefix.Length..];
        }
        else if (key.StartsWith(KubernetesJobParameterKeys.EnvFromConfigMapPrefix, StringComparison.Ordinal))
        {
            kind = KubernetesEnvSourceKind.ConfigMap;
            ordinal = key[KubernetesJobParameterKeys.EnvFromConfigMapPrefix.Length..];
        }
        else
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidEnvFromKey,
                $"Kubernetes parameter '{key}' is not a recognized envFrom source; use "
                + $"'{KubernetesJobParameterKeys.EnvFromSecretPrefix}<n>' or "
                + $"'{KubernetesJobParameterKeys.EnvFromConfigMapPrefix}<n>'."));
            return;
        }

        if (ordinal.Length == 0 || !ordinal.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidEnvFromKey,
                $"Kubernetes parameter '{key}' must end in an ordinal/label of letters, digits, '-' or '_' "
                + "(for example '.0')."));
            return;
        }

        var source = ParseEnvFromValue(key, kind, value, failures);
        if (source is not null)
        {
            envFrom.Add((ordinal, kind, source));
        }
    }

    private static KubernetesEnvFromSource? ParseEnvFromValue(
        string key,
        KubernetesEnvSourceKind kind,
        string? value,
        List<KubernetesJobParameterException> failures)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        var name = separator < 0 ? trimmed : trimmed[..separator];
        var prefix = separator < 0 ? null : trimmed[(separator + 1)..];

        if (!IsDns1123Subdomain(name))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidObjectName,
                $"Kubernetes parameter '{key}' must name a {Describe(kind)} as '<name>' or '<name>:<prefix>', where "
                + "<name> is a DNS-1123 subdomain (lowercase letters, digits, '-' and '.', at most 253 characters)."));
            return null;
        }

        if (prefix is not null && !IsEnvironmentVariableName(prefix))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidEnvFromPrefix,
                $"Kubernetes parameter '{key}' has an invalid envFrom prefix; a prefix must be a valid environment "
                + "variable name fragment (letters, digits, '_', '-', '.', not starting with a digit)."));
            return null;
        }

        return new KubernetesEnvFromSource(kind, name, prefix);
    }

    private static void ResolveKeyReference(
        string key,
        string variableName,
        KubernetesEnvSourceKind kind,
        string? value,
        Dictionary<string, string> literalKeys,
        Dictionary<string, string> referenceKeys,
        Dictionary<string, KubernetesEnvKeyReference> references,
        List<KubernetesJobParameterException> failures)
    {
        if (!IsEnvironmentVariableName(variableName))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidEnvironmentVariableName,
                $"Kubernetes parameter '{key}' does not name a valid environment variable "
                + "(letters, digits, '_', '-', '.', not starting with a digit)."));
            return;
        }

        if (ExecutionWorkerLaunchEnvironment.IsReserved(variableName))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.ReservedEnvironmentVariable,
                $"Kubernetes parameter '{key}' targets the reserved execution-worker launch variable "
                + $"'{variableName}', which the backend always sets literally."));
            return;
        }

        if (literalKeys.TryGetValue(variableName, out var literalKey) ||
            referenceKeys.TryGetValue(variableName, out literalKey))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.ConflictingEnvironmentVariable,
                $"Kubernetes parameter '{key}' defines environment variable '{variableName}', which "
                + $"'{literalKey}' already defines; declare each variable once."));
            return;
        }

        var trimmed = value?.Trim() ?? string.Empty;
        var separator = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidKeyReference,
                $"Kubernetes parameter '{key}' must reference a {Describe(kind)} key as '<name>:<key>'."));
            return;
        }

        var objectName = trimmed[..separator];
        var dataKey = trimmed[(separator + 1)..];
        if (!IsDns1123Subdomain(objectName))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidObjectName,
                $"Kubernetes parameter '{key}' must reference a {Describe(kind)} whose name is a DNS-1123 subdomain "
                + "(lowercase letters, digits, '-' and '.', at most 253 characters)."));
            return;
        }

        if (!IsDataKey(dataKey))
        {
            failures.Add(new(
                key,
                KubernetesJobParameterError.InvalidKeyReference,
                $"Kubernetes parameter '{key}' must reference a {Describe(kind)} data key of letters, digits, "
                + "'-', '_' and '.' (at most 253 characters)."));
            return;
        }

        references[variableName] = new KubernetesEnvKeyReference(kind, objectName, dataKey);
        referenceKeys[variableName] = key;
    }

    // Matches the value-source suffix case-insensitively so a misspelled casing is refused instead of
    // silently becoming a literal variable named "<NAME>.secretkeyref".
    private static bool TryGetValueSourceSuffix(string key, out string suffix, out bool exactSpelling)
    {
        foreach (var candidate in new[]
                 {
                     KubernetesJobParameterKeys.SecretKeyRefSuffix,
                     KubernetesJobParameterKeys.ConfigMapKeyRefSuffix
                 })
        {
            if (key.StartsWith(KubernetesJobParameterKeys.EnvironmentPrefix, StringComparison.Ordinal) &&
                key.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                suffix = candidate;
                exactSpelling = key.EndsWith(candidate, StringComparison.Ordinal);
                return true;
            }
        }

        suffix = string.Empty;
        exactSpelling = false;
        return false;
    }

    private static string Describe(KubernetesEnvSourceKind kind)
        => kind == KubernetesEnvSourceKind.Secret ? "Secret" : "ConfigMap";

    // RFC 1123 subdomain, the shape Kubernetes requires for Secret and ConfigMap names.
    internal static bool IsDns1123Subdomain(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253)
        {
            return false;
        }

        foreach (var label in value.Split('.'))
        {
            if (label.Length == 0 || !IsLowerAlphanumeric(label[0]) || !IsLowerAlphanumeric(label[^1]))
            {
                return false;
            }

            if (!label.All(ch => IsLowerAlphanumeric(ch) || ch == '-'))
            {
                return false;
            }
        }

        return true;
    }

    // Kubernetes' environment variable name rule ([-._a-zA-Z][-._a-zA-Z0-9]*).
    internal static bool IsEnvironmentVariableName(string? value)
    {
        if (string.IsNullOrEmpty(value) || char.IsAsciiDigit(value[0]))
        {
            return false;
        }

        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');
    }

    // Secret/ConfigMap data keys: [-._a-zA-Z0-9]+, at most 253 characters.
    private static bool IsDataKey(string value)
        => value.Length is > 0 and <= 253
           && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.');

    private static bool IsLowerAlphanumeric(char ch) => char.IsAsciiDigit(ch) || char.IsAsciiLetterLower(ch);

    // Orders envFrom ordinals numerically when both are integers ("2" before "10"), ordinally otherwise.
    private sealed class EnvFromOrdinalComparer : IComparer<string>
    {
        public static readonly EnvFromOrdinalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var left) &&
                int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var right))
            {
                return left.CompareTo(right);
            }

            return string.CompareOrdinal(x, y);
        }
    }
}
