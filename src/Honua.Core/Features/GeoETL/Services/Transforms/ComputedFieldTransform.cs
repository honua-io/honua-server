// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 computed / calculated field transform. Adds a new attribute derived from the
/// existing attributes via a small, AOT-safe operation set — no expression engine, no
/// reflection. Streaming and constant-memory.
/// </summary>
/// <remarks>
/// Required <see cref="TransformConfig.Options"/>:
/// <list type="bullet">
/// <item><c>target</c> — the attribute name to write the computed value to.</item>
/// <item><c>op</c> — one of <c>concat</c>, <c>add</c>, <c>subtract</c>, <c>multiply</c>,
/// <c>divide</c>, <c>const</c>.</item>
/// </list>
/// For <c>concat</c>: <c>fields</c> (comma-separated source field names) joined by an
/// optional <c>separator</c>. For the arithmetic ops: <c>left</c> and <c>right</c>, each
/// either a source field name or, when prefixed with <c>=</c>, a numeric literal. For
/// <c>const</c>: <c>value</c> (a literal string assigned to <c>target</c>). Rows whose
/// arithmetic operands are non-numeric are dropped as row-level data errors (ADR-0038).
/// </remarks>
public sealed class ComputedFieldTransform : IPipelineTransform, ISchemaAwareTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "computed-field";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public TransformSchemaEffect DescribeSchema(TransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var target = RequireOption(config, "target");
        var op = RequireOption(config, "op").ToLowerInvariant();
        var required = ResolveRequiredFields(config, op);
        return new TransformSchemaEffect(RequiredFields: required, ProducedFields: [target], RemovedFields: []);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var target = RequireOption(config, "target");
        var op = RequireOption(config, "op").ToLowerInvariant();

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var attributes = feature.Attributes ?? new AttributesTable();
            if (TryCompute(config, op, attributes, out var value))
            {
                if (attributes.Exists(target))
                {
                    attributes[target] = value!;
                }
                else
                {
                    attributes.Add(target, value);
                }

                yield return new Feature(feature.Geometry, attributes);
            }

            // else: row-level data error (non-numeric operand) — drop the row.
        }
    }

    private static bool TryCompute(
        TransformConfig config,
        string op,
        IAttributesTable attributes,
        out object? value)
    {
        value = null;

        if (op == "const")
        {
            value = config.Options.TryGetValue("value", out var constValue) ? constValue : string.Empty;
            return true;
        }

        if (op == "concat")
        {
            var fields = SplitFields(config, "fields");
            var separator = config.Options.TryGetValue("separator", out var sep) ? sep : string.Empty;
            var parts = fields.Select(f =>
                attributes.Exists(f)
                    ? Convert.ToString(attributes.GetOptionalValue(f), CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty);
            value = string.Join(separator, parts);
            return true;
        }

        if (!TryReadOperand(config, "left", attributes, out var left) ||
            !TryReadOperand(config, "right", attributes, out var right))
        {
            return false;
        }

        double result;
        switch (op)
        {
            case "add":
                result = left + right;
                break;
            case "subtract":
                result = left - right;
                break;
            case "multiply":
                result = left * right;
                break;
            case "divide":
                if (right == 0)
                {
                    return false;
                }

                result = left / right;
                break;
            default:
                throw new InvalidOperationException(
                    $"Computed-field operation '{op}' is not supported. " +
                    "Supported: concat, add, subtract, multiply, divide, const.");
        }

        value = result;
        return true;
    }

    private static bool TryReadOperand(
        TransformConfig config,
        string key,
        IAttributesTable attributes,
        out double result)
    {
        result = 0;
        var token = RequireOption(config, key);

        if (token.StartsWith('='))
        {
            return double.TryParse(
                token.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        if (!attributes.Exists(token))
        {
            return false;
        }

        return double.TryParse(
            Convert.ToString(attributes.GetOptionalValue(token), CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static IReadOnlyList<string> ResolveRequiredFields(TransformConfig config, string op)
    {
        switch (op)
        {
            case "const":
                return [];
            case "concat":
                return SplitFields(config, "fields");
            default:
                var required = new List<string>(2);
                AddFieldOperand(config, "left", required);
                AddFieldOperand(config, "right", required);
                return required;
        }
    }

    private static void AddFieldOperand(TransformConfig config, string key, List<string> into)
    {
        var token = RequireOption(config, key);
        if (!token.StartsWith('='))
        {
            into.Add(token);
        }
    }

    private static string[] SplitFields(TransformConfig config, string key)
    {
        var raw = RequireOption(config, key);
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string RequireOption(TransformConfig config, string key)
    {
        if (!config.Options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Computed-field transform requires a '{key}' option.");
        }

        return value;
    }
}
