using Microsoft.Extensions.Options;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Jms;

// Jms settings validation, JMS links, and the up/down status the banner and cache rely on.
public class JmsConfigTests
{
    private static JmsOptions ValidOptions() => FakeJms.CreateOptions("https://jms.example.com/");

    // ── JmsOptionsValidator ──

    [Fact]
    public void CompleteSettings_AreValid()
    {
        Assert.True(new JmsOptionsValidator().Validate(null, ValidOptions()).Succeeded);
    }

    [Fact]
    public void MissingEndpoint_IsNamedInTheError()
    {
        var options = ValidOptions();
        options.Endpoints.CreateJob = "";

        var result = new JmsOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Endpoints:CreateJob", result.FailureMessage);
    }

    [Fact]
    public void MissingServiceAccountAndBadUrl_AreAllListed()
    {
        var options = ValidOptions();
        options.BaseUrl = "not a url";
        options.ServiceAccount.Password = "";

        var result = new JmsOptionsValidator().Validate(null, options);

        Assert.Contains("BaseUrl", result.FailureMessage);
        Assert.Contains("ServiceAccount:Password", result.FailureMessage);
    }

    [Theory]
    [InlineData("https://jms.example.com")]
    [InlineData("https://jms.example.com/")]
    public void BaseUri_AlwaysEndsWithASlash(string baseUrl)
    {
        var options = ValidOptions();
        options.BaseUrl = baseUrl;
        Assert.Equal("https://jms.example.com/", options.BaseUri.ToString());
    }

    // ── JmsLinks ──

    [Fact]
    public void JobLink_IsBuiltFromTheConfiguredPage()
    {
        var links = new JmsLinks(Options.Create(ValidOptions()));

        Assert.Equal("https://jms.example.com/job?id=1001", links.JobSummary(1001));
        Assert.Equal("https://jms.example.com/job?id=1001", links.JobSummary("1001"));
    }

    [Fact]
    public void JobLink_EscapesTheId()
    {
        var links = new JmsLinks(Options.Create(ValidOptions()));
        Assert.Equal("https://jms.example.com/job?id=1%26x%3D2", links.JobSummary("1&x=2"));
    }

    // ── JmsStatus ──

    [Fact]
    public void Status_IsUnknownUntilTheFirstReport()
    {
        Assert.Null(new JmsStatus().IsUp);
    }

    [Fact]
    public void Status_RecordsWhenAnOutageStarted_AndClearsItWhenBack()
    {
        var status = new JmsStatus();
        var down = new DateTime(2026, 9, 28, 10, 42, 0);

        status.Report(false, down);
        Assert.False(status.IsUp);
        Assert.Equal(down, status.DownSince);

        status.Report(true, down.AddMinutes(5));
        Assert.True(status.IsUp);
        Assert.Null(status.DownSince);
    }

    [Fact]
    public void Status_FiresChangedOnlyWhenItFlips()
    {
        var status = new JmsStatus();
        var changes = 0;
        status.Changed += () => changes++;

        status.Report(true, DateTime.Now);
        status.Report(true, DateTime.Now);
        status.Report(false, DateTime.Now);
        status.Report(false, DateTime.Now);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void Status_KeepsTheOriginalOutageStart_OnRepeatedDownReports()
    {
        var status = new JmsStatus();
        var first = new DateTime(2026, 9, 28, 10, 0, 0);

        status.Report(false, first);
        status.Report(false, first.AddMinutes(1));

        Assert.Equal(first, status.DownSince);
    }
}
