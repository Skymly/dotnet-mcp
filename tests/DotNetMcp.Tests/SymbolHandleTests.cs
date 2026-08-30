using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class SymbolHandleTests
{
    [Fact]
    public void create_format_tryparse_roundtrips_fields_with_12_lowercase_hex_checksum()
    {
        var created = SymbolHandle.Create("csharp", "proj-id", "Ns.Type.Member");

        Assert.Equal(12, created.Checksum.Length);
        Assert.All(created.Checksum, c => Assert.True(char.IsAsciiHexDigitLower(c)));
        Assert.Equal($"csharp:proj-id:Ns.Type.Member#{created.Checksum}", created.Format());

        Assert.True(SymbolHandle.TryParse(created.Format(), out var parsed, out var error));
        Assert.Null(error);
        Assert.NotNull(parsed);
        Assert.Equal("csharp", parsed!.Language);
        Assert.Equal("proj-id", parsed.ProjectId);
        Assert.Equal("Ns.Type.Member", parsed.SignatureQualifiedName);
        Assert.Equal(created.Checksum, parsed.Checksum);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void tryparse_empty_returns_error_string(string? raw)
    {
        Assert.False(SymbolHandle.TryParse(raw!, out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Handle is empty.", error);
    }

    [Theory]
    [InlineData("csharp:proj:Ns.Type")]
    [InlineData("csharp:proj:Ns.Type#")]
    [InlineData("#0123456789ab")]
    public void tryparse_missing_checksum_returns_error_string(string raw)
    {
        Assert.False(SymbolHandle.TryParse(raw, out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Handle must end with #<checksum>.", error);
    }

    [Theory]
    [InlineData("csharp:proj:Ns.Type#short")]
    [InlineData("csharp:proj:Ns.Type#0123456789abcdef")]
    [InlineData("csharp:proj:Ns.Type#xxxxxxxxxxxx")]
    public void tryparse_illegal_checksum_returns_error_string(string raw)
    {
        Assert.False(SymbolHandle.TryParse(raw, out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Checksum must be 12 lowercase hex characters.", error);
    }

    [Fact]
    public void tryparse_missing_language_returns_error_string()
    {
        Assert.False(SymbolHandle.TryParse(":proj:Ns.Type#0123456789ab", out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Handle must start with {language}:{projectId}:...", error);
    }

    [Fact]
    public void tryparse_missing_project_id_returns_error_string()
    {
        Assert.False(SymbolHandle.TryParse("csharp:Ns.Type#0123456789ab", out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Handle must include projectId and signatureQualifiedName.", error);
    }

    [Fact]
    public void tryparse_missing_signature_returns_error_string()
    {
        Assert.False(SymbolHandle.TryParse("csharp:proj:#0123456789ab", out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Handle must include projectId and signatureQualifiedName.", error);
    }

    [Fact]
    public void tryparse_checksum_mismatch_returns_error_string()
    {
        Assert.False(SymbolHandle.TryParse("csharp:proj:Ns.Type#0123456789ab", out var handle, out var error));
        Assert.Null(handle);
        Assert.Equal("Checksum does not match handle fields.", error);
    }
}
