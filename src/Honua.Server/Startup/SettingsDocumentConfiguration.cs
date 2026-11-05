// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Honua.Server.Startup;

// A failure names the document reference and never echoes the document content.
internal static partial class SettingsDocumentConfiguration
{
    public const string EnvironmentVariableName = "HONUA_SETTINGS_DOCUMENT";

    public const string ConfigurationKey = "Settings:Document";

    public const int MaxDocumentBytes = 64 * 1024;

    private const string AwsSecretsManagerScheme = "aws:secretsmanager:";

    public static async Task<int?> AddSettingsDocumentAsync(
        ConfigurationManager configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (GetDocumentReference(configuration) is null)
        {
            return null;
        }

        using var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
        using var lease = BootstrapSecretResolver.Create();
        return await AddSettingsDocumentAsync(
                configuration,
                lease.Resolver,
                loggerFactory.CreateLogger(typeof(SettingsDocumentConfiguration).FullName!),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<int?> AddSettingsDocumentAsync(
        ConfigurationManager configuration,
        IConnectionSecretResolver resolver,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(logger);

        var reference = GetDocumentReference(configuration);
        if (reference is null)
        {
            return null;
        }

        var content = await FetchAsync(reference, resolver, cancellationToken).ConfigureAwait(false);
        var data = Parse(reference, content);
        Insert(configuration, new SettingsDocumentConfigurationSource(reference, data));
        LogLoaded(logger, data.Count, reference);
        return data.Count;
    }

    // Both names set to different values refuse startup, so the loaded document is unambiguous.
    internal static string? GetDocumentReference(IConfiguration configuration)
    {
        var fromVariable = Normalize(configuration[EnvironmentVariableName]);
        var fromKey = Normalize(configuration[ConfigurationKey]);
        if (fromVariable is not null && fromKey is not null &&
            !string.Equals(fromVariable, fromKey, StringComparison.Ordinal))
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.AmbiguousReference,
                fromVariable,
                $"Startup refused: {EnvironmentVariableName} ('{fromVariable}') and Settings__Document " +
                $"('{fromKey}') name different settings documents. Set only one of them.");
        }

        return fromVariable ?? fromKey;

        static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static async Task<string> FetchAsync(
        string reference,
        IConnectionSecretResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!reference.StartsWith(AwsSecretsManagerScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.UnsupportedReference,
                reference,
                $"Startup refused: the settings document reference '{reference}' is not supported. " +
                $"Use '{AwsSecretsManagerScheme}<secret-name-or-arn>'.");
        }

        if (!resolver.CanResolve(reference))
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.Unreadable,
                reference,
                $"Startup refused: the settings document '{reference}' cannot be resolved. " +
                "A secret name needs AWS_REGION (or AWS_DEFAULT_REGION); an ARN carries its own region.");
        }

        string? content;
        try
        {
            content = await resolver.ResolveSecretAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.Unreadable,
                reference,
                $"Startup refused: the settings document '{reference}' could not be read from AWS Secrets Manager. " +
                "Check that the secret exists and that the execution role has secretsmanager:GetSecretValue on it.",
                ex);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.Unreadable,
                reference,
                $"Startup refused: the settings document '{reference}' is empty.");
        }

        return content;
    }

    internal static Dictionary<string, string?> Parse(string reference, string content)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(content);

        if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes)
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.TooLarge,
                reference,
                $"Startup refused: the settings document '{reference}' is larger than {MaxDocumentBytes / 1024} KB.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            // Report the position only: the exception message itself may quote document bytes.
            throw new SettingsDocumentException(
                SettingsDocumentFailure.NotJson,
                reference,
                $"Startup refused: the settings document '{reference}' is not valid JSON " +
                $"(line {ex.LineNumber + 1}, byte {ex.BytePositionInLine + 1}).");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SettingsDocumentException(
                    SettingsDocumentFailure.NotObject,
                    reference,
                    $"Startup refused: the settings document '{reference}' must be a JSON object of " +
                    $"configuration keys, not a JSON {document.RootElement.ValueKind.ToString().ToLowerInvariant()}.");
            }

            var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            VisitObject(reference, document.RootElement, prefix: null, data);
            return data;
        }
    }

    private static void VisitObject(
        string reference,
        JsonElement element,
        string? prefix,
        Dictionary<string, string?> data)
    {
        var any = false;
        foreach (var property in element.EnumerateObject())
        {
            any = true;
            var name = property.Name.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
            VisitValue(reference, property.Value, Combine(prefix, name), data);
        }

        if (!any && prefix is not null)
        {
            Set(reference, prefix, null, data);
        }
    }

    private static void VisitValue(
        string reference,
        JsonElement element,
        string key,
        Dictionary<string, string?> data)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                VisitObject(reference, element, key, data);
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    VisitValue(reference, item, Combine(key, index.ToString(System.Globalization.CultureInfo.InvariantCulture)), data);
                    index++;
                }

                if (index == 0)
                {
                    Set(reference, key, null, data);
                }

                break;
            case JsonValueKind.String:
                Set(reference, key, element.GetString(), data);
                break;
            case JsonValueKind.Number:
                Set(reference, key, element.GetRawText(), data);
                break;
            case JsonValueKind.True:
                Set(reference, key, bool.TrueString, data);
                break;
            case JsonValueKind.False:
                Set(reference, key, bool.FalseString, data);
                break;
            default:
                Set(reference, key, null, data);
                break;
        }
    }

    private static string Combine(string? prefix, string name)
        => prefix is null ? name : ConfigurationPath.Combine(prefix, name);

    private static void Set(string reference, string key, string? value, Dictionary<string, string?> data)
    {
        if (string.Equals(key, EnvironmentVariableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, ConfigurationKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsDocumentException(
                SettingsDocumentFailure.ForbiddenKey,
                reference,
                $"Startup refused: the settings document '{reference}' sets '{key}'. " +
                "A settings document cannot name another settings document.");
        }

        if (!data.TryAdd(key, value))
        {
            // Key names are configuration paths, not values, so naming the duplicate is safe.
            throw new SettingsDocumentException(
                SettingsDocumentFailure.DuplicateKey,
                reference,
                $"Startup refused: the settings document '{reference}' sets '{key}' more than once " +
                "(keys are case-insensitive, and 'A__B' is the same key as nested 'A': { 'B' }).");
        }
    }

    internal static void Insert(ConfigurationManager configuration, IConfigurationSource source)
    {
        IList<IConfigurationSource> sources = ((IConfigurationBuilder)configuration).Sources;
        var insertIndex = -1;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                insertIndex = i;
                break;
            }
        }

        if (insertIndex < 0)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is CommandLineConfigurationSource)
                {
                    insertIndex = i;
                    break;
                }
            }
        }

        if (insertIndex < 0)
        {
            sources.Add(source);
        }
        else
        {
            sources.Insert(insertIndex, source);
        }
    }

    [LoggerMessage(
        EventId = 5190,
        EventName = "SettingsDocumentLoaded",
        Level = LogLevel.Information,
        Message = "Loaded {KeyCount} configuration keys from settings document {DocumentReference}; environment variables override them.")]
    private static partial void LogLoaded(ILogger logger, int keyCount, string documentReference);
}

internal enum SettingsDocumentFailure
{
    UnsupportedReference,

    AmbiguousReference,

    Unreadable,

    TooLarge,

    NotJson,

    NotObject,

    DuplicateKey,

    ForbiddenKey,
}

// The message names the reference and never contains a configuration value.
internal sealed class SettingsDocumentException : InvalidOperationException
{
    public SettingsDocumentException(
        SettingsDocumentFailure failure,
        string documentReference,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        DocumentReference = documentReference;
    }

    public SettingsDocumentFailure Failure { get; }

    public string DocumentReference { get; }
}

internal sealed class SettingsDocumentConfigurationSource(
    string documentReference,
    IReadOnlyDictionary<string, string?> data) : IConfigurationSource
{
    public string DocumentReference { get; } = documentReference;

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SettingsDocumentConfigurationProvider(DocumentReference, data);
}

// Debug views name only the document reference.
internal sealed class SettingsDocumentConfigurationProvider(
    string documentReference,
    IReadOnlyDictionary<string, string?> data) : ConfigurationProvider
{
    public override void Load()
        => Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => $"SettingsDocument({documentReference})";
}
