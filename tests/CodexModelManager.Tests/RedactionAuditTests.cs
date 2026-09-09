using CodexModelManager.Core.Security;

namespace CodexModelManager.Tests;

public sealed class RedactionAuditTests
{
    [Theory]
    [InlineData("experimental_bearer_token = \"\"\"FAKE_SECRET\nSECOND_SECRET\"\"\"")]
    [InlineData("experimental_bearer_token = '''FAKE_SECRET\nSECOND_SECRET'''")]
    [InlineData("experimental_bearer_token = \"FAKE_SECRET'OTHER_SECRET\"")]
    [InlineData("experimental_bearer_token = 'FAKE_SECRET\"OTHER_SECRET'")]
    [InlineData("experimental_bearer_token = FAKE_SECRET")]
    [InlineData("experimental_bearer_token = \"\"\"FAKE_SECRET\nSECOND_SECRET")]
    [InlineData("{\"authorization\":\"Bearer FAKE_SECRET\"}")]
    [InlineData("{\"api_key\":\"FAKE_SECRET\\\"OTHER_SECRET\"}")]
    public void StructuredSecretValuesAreFullyRedacted(string input)
    {
        string result = new SecretRedactor().Redact(input);
        Assert.DoesNotContain("FAKE_SECRET", result, StringComparison.Ordinal);
        Assert.DoesNotContain("SECOND_SECRET", result, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER_SECRET", result, StringComparison.Ordinal);
        Assert.Contains("redacted", result, StringComparison.Ordinal);
    }
}
