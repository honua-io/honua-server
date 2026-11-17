// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.AttributeRules;

public sealed partial class AttributeRuleWarningSink(ILogger logger) : IUnsupportedExpressionSink
{
    public void OnUnsupported(MetadataV2Object resource, MetadataV2AttributeRule rule)
        => Unsupported(logger, resource.Metadata.Id, rule.Name, rule.Type.ToString());

    [LoggerMessage(EventId = 7633, Level = LogLevel.Warning,
        Message = "Attribute rule {RuleName} ({RuleType}) on resource {ResourceId} uses an unsupported expression and was skipped")]
    private static partial void Unsupported(ILogger logger, string resourceId, string ruleName, string ruleType);
}
