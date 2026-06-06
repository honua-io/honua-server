// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal sealed class OperateObservabilityFixtureOptions
{
    public const string SectionName = "OperateObservabilityFixture";

    public bool Enabled { get; set; }

    public string Profile { get; set; } = OperateObservabilityFixtureConstants.Profile;

    public bool SeedOnStartup { get; set; }

    /// <summary>
    /// Resolves the effective enabled state by honouring either the nested
    /// <c>OperateObservabilityFixture:Enabled</c> config key or the flat
    /// <see cref="OperateObservabilityFixtureConstants.EnableEnvironmentVariable"/> alias.
    /// </summary>
    /// <remarks>
    /// The flat alias makes the seed path a single documented switch for CI and Console
    /// Testcontainers (#1229). It can only turn the feature on; it never disables an explicit
    /// <c>OperateObservabilityFixture:Enabled=true</c>.
    /// </remarks>
    public static bool ResolveEnabled(IConfiguration configuration, bool boundEnabled)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return boundEnabled ||
               configuration.GetValue(OperateObservabilityFixtureConstants.EnableEnvironmentVariable, false);
    }
}

internal sealed class OperateObservabilityFixtureOptionsValidator(
    IHostEnvironment environment,
    IConfiguration configuration) : IValidateOptions<OperateObservabilityFixtureOptions>
{
    public ValidateOptionsResult Validate(string? name, OperateObservabilityFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>(capacity: 3);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Test"))
        {
            failures.Add("Operate observability fixtures can only be enabled in Development or Test environments.");
        }

        if (!string.Equals(
                options.Profile,
                OperateObservabilityFixtureConstants.Profile,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"Operate observability fixture profile must be '{OperateObservabilityFixtureConstants.Profile}'.");
        }

        var provider = configuration.GetValue<string>("DataSource:Provider");
        if (provider is not null &&
            (string.Equals(provider, "duckdb", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(provider, "mysql", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(provider, "mariadb", StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("Operate observability fixtures require the PostgreSQL provider.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
