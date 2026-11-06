// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Security.Admission;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialLimits
{
    public long Tokens { get; init; }
    public long Money { get; init; }
    [JsonIgnore]
    public bool IsValid => Tokens is > 0 and <= Maximum && Money is > 0 and <= Maximum;
    internal const long Maximum = 1_000_000_000_000_000;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialPolicy
{
    public string Currency { get; init; } = string.Empty;
    public long MoneyScale { get; init; }
    public DateTimeOffset PeriodAnchorUtc { get; init; }
    public int PeriodSeconds { get; init; }
    public AiFinancialLimits Deployment { get; init; } = new();
    public AiFinancialLimits Actor { get; init; } = new();
    public AiFinancialLimits Cohort { get; init; } = new();
    public AiFinancialLimits Workflow { get; init; } = new();
    public int JournalCapacity { get; init; }
    public AiFinancialPriceBasis Basis { get; init; } = new();
    [JsonIgnore]
    public bool IsValid => Currency is { Length: 3 } && Currency.All(c => c is >= 'A' and <= 'Z')
        && MoneyScale is 1 or 10 or 100 or 1_000 or 10_000 or 100_000 or 1_000_000 or 10_000_000 or 100_000_000 or 1_000_000_000
        && PeriodAnchorUtc >= DateTimeOffset.UnixEpoch && PeriodAnchorUtc.Offset == TimeSpan.Zero
        && PeriodSeconds is >= 60 and <= 2_678_400 && JournalCapacity is > 0 and <= 8192
        && Deployment is { IsValid: true } && Actor is { IsValid: true } && Cohort is { IsValid: true }
        && Workflow is { IsValid: true } && Basis is { IsValid: true };

    public bool HasSameAccountingIdentity(AiFinancialPolicy other) => Currency == other.Currency && MoneyScale == other.MoneyScale
        && PeriodAnchorUtc == other.PeriodAnchorUtc && PeriodSeconds == other.PeriodSeconds;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialPriceBasis
{
    public string Id { get; init; } = string.Empty;
    public string SourceDigest { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string BillingSchema { get; init; } = string.Empty;
    public string ServiceTier { get; init; } = string.Empty;
    public string UseCase { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public DateTimeOffset EffectiveUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public int MaximumRequestBytes { get; init; }
    public long MaximumInputTokens { get; init; }
    public int MaximumOutputTokens { get; init; }
    public long InputRatePerMillion { get; init; }
    public long OutputRatePerMillion { get; init; }
    [JsonIgnore]
    public bool IsValid => Safe(Id, 128) && SourceDigest is { Length: 64 } && SourceDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && Safe(Provider, 128) && Kind == "openai" && BillingSchema == "openai-basic-text-v1" && ServiceTier == "default" && UseCase == "reporting-narrative" && Safe(Model, 256) && CanonicalDestination(Destination) is { } canonical && canonical == Destination
        && EffectiveUtc >= DateTimeOffset.UnixEpoch && ExpiresUtc > EffectiveUtc
        && EffectiveUtc.Offset == TimeSpan.Zero && ExpiresUtc.Offset == TimeSpan.Zero
        && MaximumRequestBytes is > 0 and <= 1_048_576 && MaximumInputTokens is > 0 and <= 100_000_000
        && MaximumOutputTokens is > 0 and <= 65_536
        && InputRatePerMillion is > 0 and <= 1_000_000_000 && OutputRatePerMillion is > 0 and <= 1_000_000_000;

    [JsonIgnore]
    public string Digest => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        "ai-financial-basis-v1", Id, SourceDigest, Provider, Kind, BillingSchema, ServiceTier, UseCase, Destination, Model,
        EffectiveUtc.ToString("O", CultureInfo.InvariantCulture), ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
        MaximumRequestBytes.ToString(CultureInfo.InvariantCulture), MaximumInputTokens.ToString(CultureInfo.InvariantCulture),
        MaximumOutputTokens.ToString(CultureInfo.InvariantCulture), InputRatePerMillion.ToString(CultureInfo.InvariantCulture),
        OutputRatePerMillion.ToString(CultureInfo.InvariantCulture)))));

    // The charge rounds up to a fixed integer, and a checked overflow is always a denial.
    public long Charge(long input, long output)
    {
        if (input < 0 || output < 0) throw new ArgumentOutOfRangeException(nameof(input));
        // Conservative independent upward rounding covers providers that bill the two
        // dimensions separately; rounding their sum could underreserve one money unit.
        var inputNumerator = (BigInteger)input * InputRatePerMillion;
        var outputNumerator = (BigInteger)output * OutputRatePerMillion;
        var inputCharge = (inputNumerator + 999_999) / 1_000_000;
        var outputCharge = (outputNumerator + 999_999) / 1_000_000;
        return checked((long)(inputCharge + outputCharge));
    }

    // Canonicalization allows credential-free HTTPS or true-loopback HTTP and does not change path semantics.
    public static string? CanonicalDestination(string? value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))) return null;
        return uri.AbsoluteUri;
    }

    private static bool Safe(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && value.All(c => c is >= ' ' and <= '~');
}

// These invocation bytes are owned by Reporting and are never deserialized from a caller or stored as a financial receipt.
public sealed class AiFinancialRequest
{
    private readonly byte[] _body;
    public AiFinancialRequest(string provider, string destination, string model, int maximumOutputTokens, ReadOnlySpan<byte> body, string? expectedBasisDigest = null)
    {
        Provider = provider; Destination = destination; Model = model; MaximumOutputTokens = maximumOutputTokens;
        ExpectedBasisDigest = expectedBasisDigest;
        _body = body.ToArray(); RequestDigest = Convert.ToHexStringLower(SHA256.HashData(_body));
    }
    public string? ExpectedBasisDigest { get; }
    public string Provider { get; }
    public string Destination { get; }
    public string Model { get; }
    public int MaximumOutputTokens { get; }
    public string RequestDigest { get; }
    public int BodyLength => _body.Length;
    public byte[] CopyBody() => (byte[])_body.Clone();
    public bool Matches(AiFinancialPriceBasis basis) => basis.IsValid && ExpectedBasisDigest == basis.Digest && Provider == basis.Provider && Destination == basis.Destination
        && Model == basis.Model && MaximumOutputTokens > 0 && MaximumOutputTokens <= basis.MaximumOutputTokens
        && BodyLength > 0 && BodyLength <= basis.MaximumRequestBytes;
}

public sealed record AiFinancialUsage(string RequestDigest, string Model, string ProviderResponseId, long InputTokens, long OutputTokens,
    string? UnrepresentableUsageDigest = null, string? ContractBreachDigest = null, string? UnsupportedUsageDigest = null,
    bool InputTokensKnown = true, bool OutputTokensKnown = true)
{
    public bool IsContractBreach => IsDigest(ContractBreachDigest);
    // When reported usage cannot be represented safely, the full debt still stands and a stop remains mandatory.
    public bool IsUnrepresentable => IsDigest(UnrepresentableUsageDigest);
    public bool IsUnsupported => IsDigest(UnsupportedUsageDigest);
    [JsonIgnore]
    public bool IsValid => IsDigest(RequestDigest) && Model is { Length: > 0 and <= 256 }
        && ProviderResponseId is { Length: <= 256 }
        && (ProviderResponseId.Length > 0 || IsContractBreach || IsUnrepresentable || IsUnsupported)
        && ProviderResponseId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
        && InputTokens is >= 0 and <= AiFinancialLimits.Maximum && OutputTokens is >= 0 and <= AiFinancialLimits.Maximum
        && (UnrepresentableUsageDigest is null || IsUnrepresentable)
        && (ContractBreachDigest is null || IsContractBreach)
        && (UnsupportedUsageDigest is null || IsUnsupported)
        && (InputTokensKnown || InputTokens == 0) && (OutputTokensKnown || OutputTokens == 0)
        && ((InputTokensKnown && OutputTokensKnown) || IsUnsupported || IsContractBreach || IsUnrepresentable);

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
