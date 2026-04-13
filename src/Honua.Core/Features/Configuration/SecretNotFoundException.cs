namespace Honua.Core.Features.Configuration;

/// <summary>
/// Thrown when a secret reference cannot be resolved.
/// </summary>
public sealed class SecretNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SecretNotFoundException"/> class.
    /// </summary>
    /// <param name="secretReference">The unresolved secret reference.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public SecretNotFoundException(string secretReference, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        SecretReference = secretReference;
    }

    /// <summary>
    /// Gets the secret reference that could not be resolved.
    /// </summary>
    public string SecretReference { get; }
}
