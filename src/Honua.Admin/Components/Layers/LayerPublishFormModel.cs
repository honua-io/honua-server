// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Admin.Models;

namespace Honua.Admin.Components.Layers;

public sealed class LayerPublishFormModel : IValidatableObject
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string LayerName { get; set; } = string.Empty;

    public string Schema { get; set; } = string.Empty;

    public string Table { get; set; } = string.Empty;

    public string? GeometryColumn { get; set; }

    public string? GeometryType { get; set; }

    public int? Srid { get; set; }

    public string? PrimaryKey { get; set; }

    public bool Enabled { get; set; } = true;

    public List<LayerFieldOption> Fields { get; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Fields.All(field => !field.IsSelected))
        {
            yield return new ValidationResult(
                "Select at least one field to publish.",
                new[] { nameof(Fields) });
        }

        if (string.IsNullOrWhiteSpace(PrimaryKey))
        {
            yield return new ValidationResult(
                "Primary key is required.",
                new[] { nameof(PrimaryKey) });
            yield break;
        }

        var primaryField = Fields.FirstOrDefault(field =>
            field.Name.Equals(PrimaryKey, StringComparison.OrdinalIgnoreCase));

        if (primaryField == null)
        {
            yield return new ValidationResult(
                "Primary key must match a selected field.",
                new[] { nameof(PrimaryKey) });
            yield break;
        }

        if (!primaryField.IsSelected)
        {
            yield return new ValidationResult(
                "Primary key field must be selected.",
                new[] { nameof(PrimaryKey) });
        }

        if (!primaryField.IsSelectablePrimaryKey)
        {
            yield return new ValidationResult(
                "Primary key must be an integer field.",
                new[] { nameof(PrimaryKey) });
        }
    }

    public static LayerPublishFormModel FromTable(TableInfo table)
    {
        var model = new LayerPublishFormModel
        {
            Schema = table.Schema,
            Table = table.Table,
            LayerName = table.Table,
            GeometryColumn = table.GeometryColumn,
            GeometryType = table.GeometryType,
            Srid = table.Srid,
            Enabled = true
        };

        foreach (var column in table.Columns)
        {
            model.Fields.Add(new LayerFieldOption
            {
                Name = column.Name,
                DataType = column.DataType,
                IsNullable = column.IsNullable,
                IsPrimaryKey = column.IsPrimaryKey,
                MaxLength = column.MaxLength,
                IsSelected = true
            });
        }

        var primaryCandidate = model.Fields.FirstOrDefault(field => field.IsPrimaryKey && field.IsSelectablePrimaryKey)
                               ?? model.Fields.FirstOrDefault(field => field.IsSelectablePrimaryKey);

        model.PrimaryKey = primaryCandidate?.Name;
        primaryCandidate?.IsSelected = true;

        return model;
    }
}

public sealed class LayerFieldOption
{
    public string Name { get; init; } = string.Empty;

    public string DataType { get; init; } = string.Empty;

    public bool IsNullable { get; init; }

    public bool IsPrimaryKey { get; init; }

    public int? MaxLength { get; init; }

    public bool IsSelected { get; set; }

    public bool IsSelectablePrimaryKey => IsIntegerType(DataType);

    private static bool IsIntegerType(string dataType)
    {
        var normalized = dataType.Trim().ToLowerInvariant();
        return normalized is "smallint" or "integer" or "bigint";
    }
}
