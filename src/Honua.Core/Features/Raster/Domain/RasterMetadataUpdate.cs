// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Mutable descriptive metadata applied by the admin raster update (PATCH) path. Each field is
/// optional: a <c>null</c> wrapper means "leave unchanged", while a present wrapper carries the
/// new value (which may itself be <c>null</c> for nullable columns such as description and
/// acquisition date). At least one field must be set for the update to do anything.
/// </summary>
public sealed record RasterMetadataUpdate
{
    /// <summary>
    /// New display name. <c>null</c> leaves the name unchanged. The contained value, when present,
    /// must be non-empty (validated by the caller).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Wrapper around the new description. <c>null</c> leaves the description unchanged; a present
    /// wrapper sets the description (its value may be <c>null</c> to clear it).
    /// </summary>
    public Optional<string?> Description { get; init; }

    /// <summary>
    /// Wrapper around the new acquisition date. <c>null</c> leaves it unchanged; a present wrapper
    /// sets the acquisition date (its value may be <c>null</c> to clear it).
    /// </summary>
    public Optional<DateTimeOffset?> AcquisitionDate { get; init; }

    /// <summary>
    /// Indicates whether any field is set; an update with no fields is a no-op the caller can reject.
    /// </summary>
    public bool HasAnyChange => Name is not null || Description.IsSet || AcquisitionDate.IsSet;
}

/// <summary>
/// A presence-tracking optional wrapper that distinguishes "not provided" (<see cref="IsSet"/> is
/// <see langword="false"/>) from "provided with a value of null" for PATCH semantics over nullable
/// columns. Construct set/unset instances through the non-generic <see cref="Optional"/> helper.
/// </summary>
/// <typeparam name="T">Wrapped value type.</typeparam>
public readonly record struct Optional<T>
{
    internal Optional(T value, bool isSet)
    {
        Value = value;
        IsSet = isSet;
    }

    /// <summary>
    /// The wrapped value. Only meaningful when <see cref="IsSet"/> is <see langword="true"/>.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// Whether a value was explicitly provided.
    /// </summary>
    public bool IsSet { get; }
}

/// <summary>
/// Factory helpers for <see cref="Optional{T}"/> (kept non-generic so the static members do not
/// live on the generic type per CA1000).
/// </summary>
public static class Optional
{
    /// <summary>
    /// Creates a set wrapper carrying <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="T">Wrapped value type.</typeparam>
    /// <param name="value">The provided value.</param>
    /// <returns>A wrapper with <see cref="Optional{T}.IsSet"/> set to <see langword="true"/>.</returns>
    public static Optional<T> Of<T>(T value) => new(value, true);

    /// <summary>
    /// Creates an unset wrapper representing "not provided".
    /// </summary>
    /// <typeparam name="T">Wrapped value type.</typeparam>
    /// <returns>A wrapper with <see cref="Optional{T}.IsSet"/> set to <see langword="false"/>.</returns>
    public static Optional<T> Unset<T>() => default;
}
