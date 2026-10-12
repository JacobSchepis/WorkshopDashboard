using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Tests.Jms;

// JMS takes raw SQL text, so these escapes are all that stands between a search box and
// the database. JMS's MySQL reads backslash escapes, so backslashes matter as much as quotes.
public class JmsSqlTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData(@"C:\path", @"C:\\path")]
    [InlineData(@"x\", @"x\\")]                     // a trailing backslash can't escape the closing quote
    [InlineData(@"\' OR 1=1 -- ", @"\\'' OR 1=1 -- ")]
    public void Escape_DoublesQuotesAndBackslashes(string input, string expected) =>
        Assert.Equal(expected, JmsSql.Escape(input));

    [Theory]
    [InlineData("50%", @"50\\%")]
    [InlineData("a_b", @"a\\_b")]
    [InlineData(@"a\b", @"a\\\\b")]
    [InlineData("O'Brien", "O''Brien")]
    public void EscapeLike_AlsoEscapesWildcards(string input, string expected) =>
        Assert.Equal(expected, JmsSql.EscapeLike(input));
}
