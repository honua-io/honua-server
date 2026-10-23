using System.Security.Cryptography;

namespace Honua.Ci.SourceInventory;

internal sealed record CapturedInput(string Path, byte[] Bytes, string Sha256)
{
    internal static CapturedInput? Read(string path, ICollection<string> diagnostics)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return new(path, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (IOException)
        {
            diagnostics.Add("missing-evaluated-input:" + path);
            return null;
        }
    }

    internal string? VerifyUnchanged()
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path))) == Sha256
                ? null : "input-changed-during-inspection:" + Path;
        }
        catch (IOException)
        {
            return "missing-evaluated-input-after-inspection:" + Path;
        }
    }

    internal string SourceText()
    {
        using var stream = new MemoryStream(Bytes, writable: false);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
