// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Serialization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Honua.Core.Features.Security.Admission;

/// <summary>Explicit operator-supplied positive token and fixed-integer money ceilings for one scope.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialLimits
{
    /// <summary>Total input plus output token allowance.</summary>
    public long Tokens { get; init; }
    /// <summary>Allowance in the policy's explicitly selected currency scale.</summary>
    public long Money { get; init; }
    /// <summary>Whether both dimensions are positive and bounded for checked arithmetic.</summary>
    [JsonIgnore]
    public bool IsValid => Tokens is > 0 and <= Maximum && Money is > 0 and <= Maximum;
    internal const long Maximum = 1_000_000_000_000_000;
}

/// <summary>Mandatory financial policy when present; absent means legacy attempt-only enforcement.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialPolicy
{
    /// <summary>Currency identifier; no conversion or currency is inferred.</summary>
    public string Currency { get; init; } = string.Empty;
    /// <summary>Number of integer money units per whole currency unit, explicitly supplied.</summary>
    public long MoneyScale { get; init; }
    /// <summary>Fixed period anchor, supplied by the operator and evaluated using database time.</summary>
    public DateTimeOffset PeriodAnchorUtc { get; init; }
    /// <summary>Length of each anchored accounting period, between one minute and 31 days.</summary>
    public int PeriodSeconds { get; init; }
    /// <summary>Deployment ceilings.</summary>
    public AiFinancialLimits Deployment { get; init; } = new();
    /// <summary>Canonical actor ceilings.</summary>
    public AiFinancialLimits Actor { get; init; } = new();
    /// <summary>Actor/use-case ceilings.</summary>
    public AiFinancialLimits Cohort { get; init; } = new();
    /// <summary>Verified workflow ceilings, aggregating across actors.</summary>
    public AiFinancialLimits Workflow { get; init; } = new();
    /// <summary>Hard lifetime journal bound. No liability or settled history is automatically evicted.</summary>
    public int JournalCapacity { get; init; }
    /// <summary>Explicit immutable supported Reporting price and conservative input-bound basis.</summary>
    public AiFinancialPriceBasis Basis { get; init; } = new();
    /// <summary>Whether all policy values have a supported bounded shape; this does not approve prices or terms.</summary>
    [JsonIgnore]
    public bool IsValid => Currency is { Length: 3 } && Currency.All(c => c is >= 'A' and <= 'Z')
        && MoneyScale is 1 or 10 or 100 or 1_000 or 10_000 or 100_000 or 1_000_000 or 10_000_000 or 100_000_000 or 1_000_000_000
        && PeriodAnchorUtc >= DateTimeOffset.UnixEpoch && PeriodAnchorUtc.Offset == TimeSpan.Zero
        && PeriodSeconds is >= 60 and <= 2_678_400 && JournalCapacity is > 0 and <= 8192
        && Deployment is { IsValid: true } && Actor is { IsValid: true } && Cohort is { IsValid: true }
        && Workflow is { IsValid: true } && Basis is { IsValid: true };

    /// <summary>Whether an operator update preserves immutable accounting identity and period boundaries.</summary>
    public bool HasSameAccountingIdentity(AiFinancialPolicy other) => Currency == other.Currency && MoneyScale == other.MoneyScale
        && PeriodAnchorUtc == other.PeriodAnchorUtc && PeriodSeconds == other.PeriodSeconds;
}

/// <summary>Operator assertion of a supported route, rates and conservative maximum input liability, not detected provider terms.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AiFinancialPriceBasis
{
    /// <summary>Versioned operator basis identifier.</summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>Digest of the reviewed price/terms source; no source body is persisted in invocation receipts.</summary>
    public string SourceDigest { get; init; } = string.Empty;
    /// <summary>Configured provider name.</summary>
    public string Provider { get; init; } = string.Empty;
    /// <summary>Supported adapter kind; only the basic OpenAI Reporting contract is implemented.</summary>
    public string Kind { get; init; } = string.Empty;
    /// <summary>Explicit supported billable-dimension contract; no universal compatible-provider claim.</summary>
    public string BillingSchema { get; init; } = string.Empty;
    /// <summary>Explicit standard processing tier required in both request and receipt.</summary>
    public string ServiceTier { get; init; } = string.Empty;
    /// <summary>Server-selected supported workload.</summary>
    public string UseCase { get; init; } = string.Empty;
    /// <summary>Final effective invocation URL, including its exact canonical path.</summary>
    public string Destination { get; init; } = string.Empty;
    /// <summary>Exact requested and returned model identifier.</summary>
    public string Model { get; init; } = string.Empty;
    /// <summary>Earliest authoritative database time at which this basis may authorize a send.</summary>
    public DateTimeOffset EffectiveUtc { get; init; }
    /// <summary>Exclusive authoritative expiry for acquisition and send commitment.</summary>
    public DateTimeOffset ExpiresUtc { get; init; }
    /// <summary>Maximum serialized body bytes accepted by this basis.</summary>
    public int MaximumRequestBytes { get; init; }
    /// <summary>Owner-asserted conservative bound covering all model input framing and messages; not a byte estimate.</summary>
    public long MaximumInputTokens { get; init; }
    /// <summary>Maximum supported provider-enforced output tokens. A stricter Reporting option is retained.</summary>
    public int MaximumOutputTokens { get; init; }
    /// <summary>Integer money units per one million input tokens.</summary>
    public long InputRatePerMillion { get; init; }
    /// <summary>Integer money units per one million output tokens.</summary>
    public long OutputRatePerMillion { get; init; }
    /// <summary>Whether this basis has bounded supported shape, without asserting that its economic terms are approved.</summary>
    [JsonIgnore]
    public bool IsValid => Safe(Id, 128) && SourceDigest is { Length: 64 } && SourceDigest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && Safe(Provider, 128) && Kind == "openai" && BillingSchema == "openai-basic-text-v1" && ServiceTier == "default" && UseCase == "reporting-narrative" && Safe(Model, 256) && CanonicalDestination(Destination) is { } canonical && canonical == Destination
        && EffectiveUtc >= DateTimeOffset.UnixEpoch && ExpiresUtc > EffectiveUtc
        && EffectiveUtc.Offset == TimeSpan.Zero && ExpiresUtc.Offset == TimeSpan.Zero
        && MaximumRequestBytes is > 0 and <= 1_048_576 && MaximumInputTokens is > 0 and <= 100_000_000
        && MaximumOutputTokens is > 0 and <= 65_536
        && InputRatePerMillion is > 0 and <= 1_000_000_000 && OutputRatePerMillion is > 0 and <= 1_000_000_000;

    /// <summary>Digest binding every price and bound field in an unambiguous versioned order.</summary>
    [JsonIgnore]
    public string Digest => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        "ai-financial-basis-v1", Id, SourceDigest, Provider, Kind, BillingSchema, ServiceTier, UseCase, Destination, Model,
        EffectiveUtc.ToString("O", CultureInfo.InvariantCulture), ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
        MaximumRequestBytes.ToString(CultureInfo.InvariantCulture), MaximumInputTokens.ToString(CultureInfo.InvariantCulture),
        MaximumOutputTokens.ToString(CultureInfo.InvariantCulture), InputRatePerMillion.ToString(CultureInfo.InvariantCulture),
        OutputRatePerMillion.ToString(CultureInfo.InvariantCulture)))));

    /// <summary>Computes an upward-rounded fixed-integer charge; checked overflow is always a denial.</summary>
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

    /// <summary>Canonicalizes a credential-free HTTPS or true-loopback HTTP destination without changing path semantics.</summary>
    public static string? CanonicalDestination(string? value)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))) return null;
        return uri.AbsoluteUri;
    }

    private static bool Safe(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && value.All(c => c is >= ' ' and <= '~');
}

/// <summary>Exact owned immutable Reporting invocation bytes. Never deserialized from a caller or persisted as a financial receipt.</summary>
public sealed class AiFinancialRequest
{
    private readonly byte[] _body;
    /// <summary>Creates an owned copy of the exact final serialized body and route tuple.</summary>
    public AiFinancialRequest(string provider, string destination, string model, int maximumOutputTokens, ReadOnlySpan<byte> body, string? expectedBasisDigest = null)
    {
        Provider = provider; Destination = destination; Model = model; MaximumOutputTokens = maximumOutputTokens;
        ExpectedBasisDigest = expectedBasisDigest;
        _body = body.ToArray(); RequestDigest = Convert.ToHexStringLower(SHA256.HashData(_body));
    }
    /// <summary>Prepared authoritative basis digest; null is a legacy attempt-only request.</summary>
    public string? ExpectedBasisDigest { get; }
    /// <summary>Configured provider.</summary>
    public string Provider { get; }
    /// <summary>Final canonical invocation URL.</summary>
    public string Destination { get; }
    /// <summary>Exact requested model.</summary>
    public string Model { get; }
    /// <summary>Actual provider output cap encoded in the frozen body.</summary>
    public int MaximumOutputTokens { get; }
    /// <summary>Digest of the exact serialized bytes.</summary>
    public string RequestDigest { get; }
    /// <summary>Length of the exact body.</summary>
    public int BodyLength => _body.Length;
    /// <summary>Returns a fresh send copy, retaining ownership of the frozen bytes.</summary>
    public byte[] CopyBody() => (byte[])_body.Clone();
    /// <summary>Whether the invocation matches the supported policy and immutable basis bounds.</summary>
    public bool Matches(AiFinancialPriceBasis basis) => basis.IsValid && ExpectedBasisDigest == basis.Digest && Provider == basis.Provider && Destination == basis.Destination
        && Model == basis.Model && MaximumOutputTokens > 0 && MaximumOutputTokens <= basis.MaximumOutputTokens
        && BodyLength > 0 && BodyLength <= basis.MaximumRequestBytes;
}

/// <summary>Internal transport-observed usage bound to one frozen request. No public reconciliation endpoint accepts this type.</summary>
public sealed record AiFinancialUsage(string RequestDigest, string Model, string ProviderResponseId, long InputTokens, long OutputTokens,
    string? UnrepresentableUsageDigest = null, string? ContractBreachDigest = null, string? UnsupportedUsageDigest = null,
    bool InputTokensKnown = true, bool OutputTokensKnown = true)
{
    /// <summary>Whether the provider-reported model/tier or contradictory totals breached its price contract.</summary>
    public bool IsContractBreach => IsDigest(ContractBreachDigest);
    /// <summary>Whether reported numeric usage cannot be safely represented; full debt and a stop remain mandatory.</summary>
    public bool IsUnrepresentable => IsDigest(UnrepresentableUsageDigest);
    /// <summary>Whether known counters accompany unsupported dimensions and cannot fully settle liability.</summary>
    public bool IsUnsupported => IsDigest(UnsupportedUsageDigest);
    /// <summary>Whether the receipt has bounded shape; route trust belongs to the internal transport adapter.</summary>
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
