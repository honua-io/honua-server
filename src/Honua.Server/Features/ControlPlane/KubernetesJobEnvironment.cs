// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal enum KubernetesEnvSourceKind
{
    Secret,

    ConfigMap
}

internal sealed record KubernetesEnvFromSource(KubernetesEnvSourceKind Kind, string Name, string? Prefix = null);

internal sealed record KubernetesEnvKeyReference(KubernetesEnvSourceKind Kind, string Name, string Key);

internal enum KubernetesJobParameterError
{
    InvalidEnvFromKey,

    InvalidObjectName,

    InvalidEnvFromPrefix,

    InvalidKeyReference,

    InvalidEnvironmentVariableName,

    UnknownValueSource,

    ReservedEnvironmentVariable,

    ConflictingEnvironmentVariable
}

// The error names the parameter key and the reason, never a literal parameter value.
internal sealed class KubernetesJobParameterException(
    string parameterKey,
    KubernetesJobParameterError error,
    string message) : Exception(message)
{
    public string ParameterKey { get; } = parameterKey;

    public KubernetesJobParameterError Error { get; } = error;
}

internal sealed record KubernetesJobEnvironment(
    IReadOnlyDictionary<string, string> Literals,
    IReadOnlyDictionary<string, KubernetesEnvKeyReference> References,
    IReadOnlyList<KubernetesEnvFromSource> EnvFrom);

// The server handles Secret names only, and launch variables stay literal, are stamped last, and cannot be shadowed by an imported Secret.
internal static class KubernetesJobEnvironmentBuilder
{
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

        foreach (var entry in workloadEnvFrom
                     .OrderBy(entry => entry.Ordinal, EnvFromOrdinalComparer.Instance)
                     .ThenBy(entry => entry.Kind))
        {
            // A re-declared default keeps its first (lower-precedence) position; duplicates add nothing.
            if (!envFrom.Contains(entry.Source))
            {
                envFrom.Add(entry.Source);
            }
        }
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
