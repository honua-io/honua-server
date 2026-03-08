// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Alerts;

internal sealed class AlertEditionPolicy : IAlertEditionPolicy
{
    private readonly AlertOptions _options;

    public AlertEditionPolicy(IOptions<AlertOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsRuleAllowed(AlertRuleDefinition rule)
    {
        if ((int)rule.EditionRequired > (int)_options.Edition)
        {
            return false;
        }

        return _options.Edition switch
        {
            AlertEdition.Pro => rule.TriggerType is AlertTriggerType.Enter or AlertTriggerType.Exit,
            AlertEdition.Enterprise => true,
            _ => false
        };
    }

    public bool IsChannelAllowed(AlertChannelType channelType)
    {
        return _options.Edition switch
        {
            AlertEdition.Pro => channelType == AlertChannelType.Webhook,
            AlertEdition.Enterprise => true,
            _ => false
        };
    }
}
