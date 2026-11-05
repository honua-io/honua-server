// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Events;

public sealed class NatsFeatureChangeEventSinkOptions
{
    public const string SectionName = "FeatureChangeEvents:Sinks:Nats";

    public bool Enabled { get; set; }

    public string? Url { get; set; }

    public string Subject { get; set; } = "honua.feature-changes";

    public string? DeadLetterSubject { get; set; } = "honua.feature-changes.dlq";

    public bool EnableDeduplication { get; set; } = true;

    public int PublishTimeoutMs { get; set; } = 30_000;

    public string? CredsFile { get; set; }

    public string? Token { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }
}

internal sealed class NatsFeatureChangeEventSinkOptionsValidator
    : IValidateOptions<NatsFeatureChangeEventSinkOptions>
{
    public ValidateOptionsResult Validate(string? name, NatsFeatureChangeEventSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Url))
        {
            failures.Add("NATS sink Url must be configured when the NATS sink is enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Subject))
        {
            failures.Add("NATS sink Subject must be configured when the NATS sink is enabled.");
        }

        if (options.PublishTimeoutMs < 1)
        {
            failures.Add("NATS sink PublishTimeoutMs must be at least 1 millisecond.");
        }

        if (!string.IsNullOrWhiteSpace(options.DeadLetterSubject) &&
            string.Equals(options.DeadLetterSubject, options.Subject, StringComparison.Ordinal))
        {
            failures.Add("NATS sink DeadLetterSubject must differ from Subject to avoid redelivery loops.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
