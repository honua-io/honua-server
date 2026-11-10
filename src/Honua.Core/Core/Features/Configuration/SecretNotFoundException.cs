namespace Honua.Core.Features.Configuration;

public sealed class SecretNotFoundException : Exception
{
    public SecretNotFoundException(string secretReference, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        SecretReference = secretReference;
    }

    public string SecretReference { get; }
}
