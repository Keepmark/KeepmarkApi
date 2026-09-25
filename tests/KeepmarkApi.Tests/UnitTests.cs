using KeepmarkApi.Errors;
using KeepmarkApi.Routing;
using KeepmarkApi.Text;
using KeepmarkApi.Uuid;

namespace KeepmarkApi.Tests;

public sealed class SyncIdTests
{
    [Fact]
    public void Parse_AcceptsUndashed32Hex()
    {
        var bytes = SyncId.Parse("426e20b680054872a46c3e1ce9a2c54e");
        Assert.Equal(16, bytes.Length);
        Assert.Equal("426e20b680054872a46c3e1ce9a2c54e", SyncId.ToString(bytes));
    }

    [Fact]
    public void Parse_AcceptsDashedUuid()
    {
        var bytes = SyncId.Parse("426e20b6-8005-4872-a46c-3e1ce9a2c54e");
        Assert.Equal("426e20b680054872a46c3e1ce9a2c54e", SyncId.ToString(bytes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalidid")]
    [InlineData("426e20b680054872a46c3e1ce9a2c54")]   // 31 chars
    [InlineData("426e20b680054872a46c3e1ce9a2c54ex")] // non-hex
    public void Parse_RejectsMalformed(string? input)
    {
        Assert.Throws<InvalidSyncIdException>(() => SyncId.Parse(input));
    }

    [Fact]
    public void New_RoundTrips()
    {
        var id = SyncId.New();
        Assert.Equal(16, id.Length);
        Assert.Equal(id, SyncId.Parse(SyncId.ToString(id)));
    }

    [Fact]
    public void New_IsRfc4122ByteOrder()
    {
        // Guid.ToByteArray() is mixed-endian; our bytes must match the "N" hex form.
        var g = Guid.NewGuid();
        Assert.Equal(g.ToString("N"), SyncId.ToString(g.ToRfc4122Bytes()));
    }
}

public sealed class ApiVersioningTests
{
    [Theory]
    [InlineData("1.0.0", 1)]
    [InlineData("1.0.9", 1)]
    [InlineData("1.1.0", 2)]
    [InlineData("1.1.2", 2)]
    [InlineData("1.1.3", 2)]
    [InlineData("1.1.13", 2)]
    [InlineData("1.2.0", 2)]
    [InlineData("1.8.0", 2)]
    [InlineData("v1.2.0", 2)]
    public void BookmarksRouteVersion_MapsCorrectly(string header, int expected)
    {
        Assert.Equal(expected, ApiVersioning.BookmarksRouteVersion(header));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("2.0.0")]
    [InlineData("")]
    public void BookmarksRouteVersion_RejectsUnsupported(string header)
    {
        Assert.Throws<UnsupportedVersionException>(() => ApiVersioning.BookmarksRouteVersion(header));
    }
}

public sealed class StripScriptsTests
{
    [Fact]
    public void StripScripts_RemovesScriptTagsOnly()
    {
        Assert.Equal("", HtmlSanitizer.StripScripts(null));
        Assert.Equal("hello <b>world</b>",
            HtmlSanitizer.StripScripts("hello <b>world</b><script>alert(1)</script>"));
    }
}
