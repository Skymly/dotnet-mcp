using System.Security.Cryptography;
using System.Text;

namespace DotNetMcp.Core;

/// <summary>
/// LLM-stable symbol handle (ADR-0001 §1):
/// {language}:{projectId}:{signatureQualifiedName}#{checksum}
/// </summary>
public sealed record SymbolHandle(
    string Language,
    string ProjectId,
    string SignatureQualifiedName,
    string Checksum)
{
    public const int ChecksumHexLength = 12;

    public string Format() =>
        $"{Language}:{ProjectId}:{SignatureQualifiedName}#{Checksum}";

    public static string ComputeChecksum(string language, string projectId, string signatureQualifiedName)
    {
        var payload = $"{language}|{projectId}|{signatureQualifiedName}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..ChecksumHexLength];
    }

    public static SymbolHandle Create(string language, string projectId, string signatureQualifiedName)
    {
        var checksum = ComputeChecksum(language, projectId, signatureQualifiedName);
        return new SymbolHandle(language, projectId, signatureQualifiedName, checksum);
    }

    public static bool TryParse(string raw, out SymbolHandle? handle, out string? error)
    {
        handle = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Handle is empty.";
            return false;
        }

        var hashIndex = raw.LastIndexOf('#');
        if (hashIndex <= 0 || hashIndex == raw.Length - 1)
        {
            error = "Handle must end with #<checksum>.";
            return false;
        }

        var checksum = raw[(hashIndex + 1)..];
        if (checksum.Length != ChecksumHexLength ||
            checksum.Any(c => !Uri.IsHexDigit(c)))
        {
            error = "Checksum must be 12 lowercase hex characters.";
            return false;
        }

        // Prefer lowercase hex; accept uppercase input but normalize for compare.
        checksum = checksum.ToLowerInvariant();

        var body = raw[..hashIndex];
        var firstColon = body.IndexOf(':');
        if (firstColon <= 0)
        {
            error = "Handle must start with {language}:{projectId}:...";
            return false;
        }

        var secondColon = body.IndexOf(':', firstColon + 1);
        if (secondColon <= firstColon + 1 || secondColon == body.Length - 1)
        {
            error = "Handle must include projectId and signatureQualifiedName.";
            return false;
        }

        var language = body[..firstColon];
        var projectId = body[(firstColon + 1)..secondColon];
        var signature = body[(secondColon + 1)..];

        if (string.IsNullOrWhiteSpace(language) ||
            string.IsNullOrWhiteSpace(projectId) ||
            string.IsNullOrWhiteSpace(signature))
        {
            error = "Language, projectId, and signatureQualifiedName are required.";
            return false;
        }

        var expected = ComputeChecksum(language, projectId, signature);
        if (!string.Equals(expected, checksum, StringComparison.Ordinal))
        {
            error = "Checksum does not match handle fields.";
            return false;
        }

        handle = new SymbolHandle(language, projectId, signature, checksum);
        return true;
    }
}
