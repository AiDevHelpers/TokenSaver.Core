using TokenSaver.LogHelper.Models;
using TokenSaver.LogHelper.Security;
using Xunit;

namespace TokenSaver.LogHelper.Tests.Security;

public sealed class RedactorTests
{
    private static readonly LogHelperOptions Enabled = new();

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi", "<redacted:bearer>")]
    [InlineData("hdr eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl end", "eyJzdWIiOi", "<redacted:jwt>")]
    [InlineData("login password=hunter2 ok", "hunter2", "<redacted:secret>")]
    [InlineData("api_key=XYZ123 call", "XYZ123", "<redacted:secret>")]
    [InlineData("Server=db;Password=s3cret;", "s3cret", "<redacted:secret>")]
    [InlineData("user john.doe@example.com logged in", "john.doe@example.com", "<redacted:email>")]
    public void Redact_MasksSecret_WithKindMarker(string input, string secret, string marker)
    {
        string output = Redactor.Redact(input, Enabled);

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Contains(marker, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_LeavesTextUnchanged()
    {
        var options = new LogHelperOptions { RedactionEnabled = false };
        const string input = "password=hunter2 user a@b.com";

        Assert.Equal(input, Redactor.Redact(input, options));
    }

    [Fact]
    public void PlainMessage_Unchanged()
    {
        const string input = "user 42 failed at step three";

        Assert.Equal(input, Redactor.Redact(input, Enabled));
    }
}
