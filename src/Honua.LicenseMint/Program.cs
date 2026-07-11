// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.LicenseMint;

// honua-license-mint: publisher-only offline tool to (a) generate an Ed25519
// signing key pair and (b) mint Ed25519-signed Honua license envelopes that the
// runtime verifier accepts. The private signing key is the trust root for the whole
// licensing system — keep it out of version control and out of customer hands.
//
// An explicit entry class (rather than top-level statements) avoids emitting a
// synthesized global `Program` type that would collide with Honua.Server's
// `Program` when both assemblies are referenced by Honua.Server.Tests.

/// <summary>Entry point for the offline license-mint CLI.</summary>
internal static class LicenseMintProgram
{
    /// <summary>Process entry point.</summary>
    public static int Main(string[] args) => CliRunner.Run(args);
}

internal static class CliRunner
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "keygen" => RunKeygen(args[1..]),
                "mint" => RunMint(args[1..]),
                "help" or "--help" or "-h" => PrintUsageAndSucceed(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunKeygen(string[] args)
    {
        var options = ArgMap.Parse(args);
        var keyId = options.GetOrDefault("key-id", $"honua-{DateTimeOffset.UtcNow:yyyy-MM}");
        var keyPair = LicenseSigningKeyPair.Generate();

        var privateOut = options.Get("private-out");
        if (privateOut is not null)
        {
            File.WriteAllText(privateOut, keyPair.PrivateSeedBase64Url());
            TryRestrictFilePermissions(privateOut);
        }

        Console.WriteLine($"keyId:            {keyId}");
        Console.WriteLine($"publicKey:        {keyPair.PublicKeyBase64Url()}");
        Console.WriteLine($"trustedKeySetting {keyPair.PublicKeyTrustedKeySetting()}");
        Console.WriteLine();
        Console.WriteLine("Configure verification on the runtime with:");
        Console.WriteLine($"  Licensing__TrustedKeys__{keyId}={keyPair.PublicKeyTrustedKeySetting()}");
        Console.WriteLine();

        if (privateOut is not null)
        {
            Console.WriteLine($"Private seed written to: {privateOut}");
            Console.WriteLine("NEVER commit or distribute this file. It is the signing trust root.");
        }
        else
        {
            Console.Error.WriteLine("Private seed (Base64URL) — store in a secret manager, never commit:");
            Console.Error.WriteLine(keyPair.PrivateSeedBase64Url());
        }

        return 0;
    }

    private static int RunMint(string[] args)
    {
        var options = ArgMap.Parse(args);

        var keyId = options.GetRequired("key-id");
        var licenseId = options.GetRequired("license-id");
        var licensedTo = options.GetRequired("licensed-to");
        var edition = ParseEdition(options.GetRequired("edition"));
        var privateKey = LoadPrivateKey(options);

        DateTimeOffset? expiresAt = options.Get("expires") is { } expiresRaw
            ? ParseExpiry(expiresRaw)
            : null;

        var entitlements = options.Get("entitlements") is { } csv
            ? csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;
        var capacity = ParseCapacityTerms(options);

        var request = new LicenseMintRequest
        {
            KeyId = keyId,
            LicenseId = licenseId,
            LicensedTo = licensedTo,
            Edition = edition,
            ExpiresAt = expiresAt,
            Entitlements = entitlements,
            Capacity = capacity
        };

        LicenseMintResult result;
        try
        {
            result = LicenseMinter.Mint(request, privateKey);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }

        var output = options.Get("out");
        if (output is not null)
        {
            File.WriteAllBytes(output, result.EnvelopeJson);
            Console.WriteLine($"License written to: {output}");
            Console.WriteLine($"edition: {edition}; licenseId: {licenseId}; licensedTo: {licensedTo}");
            Console.WriteLine(expiresAt is { } e
                ? $"expiresAt: {e:O}"
                : "expiresAt: (perpetual)");
            if (capacity is not null)
            {
                Console.WriteLine(
                    $"capacity: {capacity.MaxSustainedServingUnits.ToString(CultureInfo.InvariantCulture)} serving units; " +
                    $"surgeDays: {capacity.AnnualSurgeDays?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"}; " +
                    $"surgeAllowance: {capacity.SurgeAllowance}");
            }
        }
        else
        {
            Console.WriteLine(LicenseMinter.ToPrettyEnvelopeJson(result));
        }

        return 0;
    }

    internal static LicenseCapacityTerms? ParseCapacityTerms(ArgMap options)
    {
        var unitsRaw = options.Get("capacity-units");
        var surgeDaysRaw = options.Get("annual-surge-days");
        var surgeAllowanceRaw = options.Get("surge-allowance");

        if (unitsRaw is null && surgeDaysRaw is null && surgeAllowanceRaw is null)
        {
            return null;
        }

        if (unitsRaw is null)
        {
            throw new CliException(
                "--capacity-units is required when annual surge days or surge allowance is specified.");
        }

        if (!decimal.TryParse(
                unitsRaw,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var capacityUnits) ||
            capacityUnits <= 0m)
        {
            throw new CliException("--capacity-units must be a number greater than zero.");
        }

        int? annualSurgeDays = 14;
        if (surgeDaysRaw is not null)
        {
            if (string.Equals(surgeDaysRaw, "unlimited", StringComparison.OrdinalIgnoreCase))
            {
                annualSurgeDays = null;
            }
            else if (int.TryParse(
                         surgeDaysRaw,
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out var parsedDays) &&
                     parsedDays >= 0)
            {
                annualSurgeDays = parsedDays;
            }
            else
            {
                throw new CliException(
                    "--annual-surge-days must be a non-negative integer or 'unlimited'.");
            }
        }

        var surgeAllowance = surgeAllowanceRaw?.ToLowerInvariant()
            ?? LicenseCapacitySurgeAllowances.Standard;
        if (surgeAllowance is not (
            LicenseCapacitySurgeAllowances.Standard or
            LicenseCapacitySurgeAllowances.High or
            LicenseCapacitySurgeAllowances.Unlimited))
        {
            throw new CliException("--surge-allowance must be standard, high, or unlimited.");
        }

        return new LicenseCapacityTerms
        {
            MaxSustainedServingUnits = capacityUnits,
            AnnualSurgeDays = annualSurgeDays,
            SurgeAllowance = surgeAllowance
        };
    }

    private static LicenseSigningKeyPair LoadPrivateKey(ArgMap options)
    {
        var fromFile = options.Get("private-key-file");
        var inline = options.Get("private-key")
            ?? Environment.GetEnvironmentVariable("HONUA_LICENSE_SIGNING_KEY");

        string seedBase64Url;
        if (fromFile is not null)
        {
            seedBase64Url = File.ReadAllText(fromFile).Trim();
        }
        else if (!string.IsNullOrWhiteSpace(inline))
        {
            seedBase64Url = inline.Trim();
        }
        else
        {
            throw new CliException(
                "No signing key. Provide --private-key-file <path>, --private-key <base64url>, " +
                "or set HONUA_LICENSE_SIGNING_KEY.");
        }

        byte[] seed;
        try
        {
            seed = Base64Url.Decode(seedBase64Url);
        }
        catch (FormatException)
        {
            throw new CliException("Signing key is not valid Base64URL.");
        }

        try
        {
            return LicenseSigningKeyPair.FromPrivateSeed(seed);
        }
        catch (ArgumentException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    private static HonuaEdition ParseEdition(string value)
    {
        if (Enum.TryParse<HonuaEdition>(value, ignoreCase: true, out var edition))
        {
            return edition;
        }

        if (string.Equals(value, "professional", StringComparison.OrdinalIgnoreCase))
        {
            return HonuaEdition.Pro;
        }

        throw new CliException($"Unknown edition '{value}'. Use Community, Pro, or Enterprise.");
    }

    private static DateTimeOffset ParseExpiry(string value)
    {
        if (value.EndsWith('d') &&
            int.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
        {
            return DateTimeOffset.UtcNow.AddDays(days);
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var absolute))
        {
            return absolute;
        }

        throw new CliException(
            $"Could not parse --expires '{value}'. Use an RFC 3339 timestamp or a duration like '365d'.");
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
            // Best-effort hardening; operators are still instructed to secure the file.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static int PrintUsageAndSucceed()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            honua-license-mint — generate signing keys and mint Honua license envelopes.

            COMMANDS
              keygen   Generate a new Ed25519 signing key pair.
              mint     Mint a signed license envelope.

            keygen options:
              --key-id <id>          Key identifier (default: honua-<yyyy-MM>).
              --private-out <path>   Write the private seed (Base64URL) to a file (chmod 600).
                                     When omitted, the private seed is printed to stderr.

            mint options:
              --key-id <id>          Trusted key id; must match a Licensing:TrustedKeys entry. (required)
              --license-id <id>      Stable license identifier. (required)
              --licensed-to <name>   Licensee / operator display name. (required)
              --edition <edition>    Community | Pro | Enterprise. (required)
              --expires <when>       RFC 3339 timestamp or duration (e.g. 365d). Omit for perpetual.
              --entitlements <keys>  Comma-separated FeatureCatalog keys. Defaults to all keys
                                     at or below the edition.
              --capacity-units <n>   Maximum sustained serving units. Omit for an unbanded license.
              --annual-surge-days <days|unlimited>
                                     Annual declared-surge days (default: 14).
              --surge-allowance <standard|high|unlimited>
                                     Contractual surge tier (default: standard).
              --private-key-file <p> File holding the Base64URL private seed.
              --private-key <b64url> Inline Base64URL private seed.
                                     (or set HONUA_LICENSE_SIGNING_KEY)
              --out <path>           Write the envelope JSON to a file. Prints to stdout otherwise.

            EXAMPLES
              honua-license-mint keygen --key-id honua-2026-q3 --private-out signing.key
              honua-license-mint mint --key-id honua-2026-q3 --license-id lic-acme-001 \
                --licensed-to "Acme Corp" --edition Pro --expires 365d \
                --capacity-units 4 --annual-surge-days 14 --surge-allowance standard \
                --private-key-file signing.key --out acme.honua-license.json
            """);
    }
}

/// <summary>A user-facing CLI error that maps to a clean exit code without a stack trace.</summary>
internal sealed class CliException : Exception
{
    public CliException(string message) : base(message)
    {
    }
}

/// <summary>Minimal <c>--flag value</c> argument parser for the mint CLI.</summary>
internal sealed class ArgMap
{
    private readonly Dictionary<string, string> _values;

    private ArgMap(Dictionary<string, string> values) => _values = values;

    public static ArgMap Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"Unexpected argument '{token}'. Options use --flag value form.");
            }

            var name = token[2..];
            if (i + 1 >= args.Length)
            {
                throw new CliException($"Option '--{name}' requires a value.");
            }

            values[name] = args[++i];
        }

        return new ArgMap(values);
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string GetOrDefault(string name, string fallback) => Get(name) ?? fallback;

    public string GetRequired(string name)
        => Get(name) ?? throw new CliException($"Missing required option '--{name}'.");
}
