using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.Jobs;

namespace WorkshopDashboard.Tests.Admin;

// Tracking notes are the NOTE lines that carry the "[Tracking] " prefix: the app writes it,
// only reads those lines back, and never shows the prefix.
public class JobNotesTests
{
    [Fact]
    public void Tag_AddsThePrefix_ToTheTrimmedText()
    {
        Assert.Equal("[Tracking] Waiting on parts", JobNotesService.Tag("  Waiting on parts "));
    }

    [Theory]
    [InlineData("[Tracking] Waiting on parts", "Waiting on parts")]
    [InlineData("[Tracking]Waiting on parts", "Waiting on parts")]
    [InlineData("  [Tracking]   Waiting on parts  ", "Waiting on parts")]
    public void Untag_StripsThePrefix(string stored, string shown)
    {
        Assert.Equal(shown, JobNotesService.Untag(stored));
    }

    [Fact]
    public void TagThenUntag_GivesBackWhatWasTyped()
    {
        Assert.Equal("Customer chasing — call back Friday", JobNotesService.Untag(JobNotesService.Tag("Customer chasing — call back Friday")));
    }

    [Fact]
    public void NotesQuery_OnlyReadsTrackingNotes_ForEveryJobAsked()
    {
        var query = JmsQueries.JobNotesQuery(new[] { 101, 102 });

        Assert.Contains("s.job_id IN (101,102)", query);
        Assert.Contains("LIKE '[Tracking]%'", query);
        Assert.Contains("service_type_id = 11", query);
    }

    [Fact]
    public void WatchlistMarker_IsNotATrackingNote()
    {
        Assert.DoesNotContain("[Tracking]", JmsQueries.ModelWatchlistIgnoredNotePrefix);
    }
}
