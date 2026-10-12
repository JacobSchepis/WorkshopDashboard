using WorkshopDashboard.Abstract;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// Reads through the shared service-account session and CachedJmsReader: always live while JMS
// answers, last good result when it doesn't.
public class JmsReadSessionTests
{
    private const string Query = "SELECT something";

    private static async Task<IJmsReader> ReaderAsync(JmsHarness h) => (await h.Reads.GetClientAsync())!;

    [Fact]
    public async Task FirstRead_LogsTheServiceAccountIn()
    {
        await using var h = await JmsHarness.StartAsync();

        var res = await (await ReaderAsync(h)).PostQueryForCsvAsync(Query);

        Assert.True(res.Success);
        Assert.Equal(1, h.Jms.LoginCount(FakeJms.ServiceAccountUsername));
    }

    [Fact]
    public async Task Queries_AlwaysGoLive_WhileJmsAnswers()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);

        var first = await reader.PostQueryForCsvAsync(Query);
        var second = await reader.PostQueryForCsvAsync(Query);

        Assert.NotEqual(first.Content, second.Content);
        Assert.Equal(2, h.Jms.QueryHits);
    }

    [Fact]
    public async Task IdenticalQueries_InFlightTogether_ShareOneRequest()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);
        await reader.PostQueryForCsvAsync("warm up the session");
        h.Jms.QueryDelay = TimeSpan.FromMilliseconds(300);
        var hitsBefore = h.Jms.QueryHits;

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => reader.PostQueryForCsvAsync(Query)));

        Assert.Equal(1, h.Jms.QueryHits - hitsBefore);
        Assert.All(results, r => Assert.Equal(results[0].Content, r.Content));
    }

    [Fact]
    public async Task WhenAQueryFails_TheLastGoodResultIsServed()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);
        var good = await reader.PostQueryForCsvAsync(Query);

        h.Jms.FailQueries = true;
        var afterFailure = await reader.PostQueryForCsvAsync(Query);

        Assert.True(afterFailure.Success);
        Assert.Equal(good.Content, afterFailure.Content);
    }

    [Fact]
    public async Task WhenJmsIsKnownDown_TheCacheIsServedWithoutAskingJms()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);
        var good = await reader.PostQueryForCsvAsync(Query);
        var hitsBefore = h.Jms.QueryHits;

        h.Status.Report(false, DateTime.Now); // JMS itself still answers — only the status says down
        var res = await reader.PostQueryForCsvAsync(Query);

        Assert.Equal(good.Content, res.Content);
        Assert.Equal(hitsBefore, h.Jms.QueryHits);
    }

    [Fact]
    public async Task WhenJmsIsDown_AndTheQueryWasNeverSeen_ItFails()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);

        await h.TakeJmsDownAsync();
        var res = await reader.PostQueryForCsvAsync("never asked before");

        Assert.False(res.Success);
    }

    [Fact]
    public async Task WhenJmsComesBack_QueriesGoLiveAgain()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);
        var beforeOutage = await reader.PostQueryForCsvAsync(Query);

        h.Status.Report(false, DateTime.Now);
        await reader.PostQueryForCsvAsync(Query);
        h.Status.Report(true, DateTime.Now);
        var afterOutage = await reader.PostQueryForCsvAsync(Query);

        Assert.NotEqual(beforeOutage.Content, afterOutage.Content);
    }

    [Fact]
    public async Task NonQueryReads_WhenJmsIsDown_ReturnEmptyValues()
    {
        await using var h = await JmsHarness.StartAsync();
        var reader = await ReaderAsync(h);

        h.Status.Report(false, DateTime.Now);

        Assert.Null(await reader.GetQuotePdfAsync("/some/quote.pdf"));
        Assert.Empty(await reader.SearchUsersAsync("alice"));
        Assert.Empty(await reader.GetCertificateAsync("cert.pdf"));
        Assert.False((await reader.GetNotYetReceivedItemsAsync(1)).Success);
    }
}
