using Microsoft.Extensions.Logging.Abstractions;
using WorkshopDashboard.Data;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Shipping;
using WorkshopDashboard.Services.Shipping;
using WorkshopDashboard.Tests.Fakes;

namespace WorkshopDashboard.Tests.Shipping;

// Carrier detection from free text, and the day-long lookup cache for DHL/TNT.
public class TrackingServiceTests
{
    /// <summary>A carrier that answers every lookup with a fixed status and counts the calls.</summary>
    private sealed class FakeCarrier : ICarrierTracker
    {
        public FakeCarrier(string carrier, string status = "In Transit") => (Carrier, Status) = (carrier, status);

        public string Carrier { get; }
        public string Status { get; set; }
        public int Lookups { get; private set; }
        public List<string> Numbers { get; } = new();

        public Task PrimeAsync() => Task.CompletedTask;

        public Task<TrackingResult> GetTrackingAsync(string trackingNumber)
        {
            Lookups++;
            Numbers.Add(trackingNumber);
            return Task.FromResult(new TrackingResult { TrackingNumber = trackingNumber, Carrier = Carrier, Status = Status });
        }
    }

    private static TrackingService Service(TestDb db, params ICarrierTracker[] carriers) =>
        new(carriers, db, NullLogger<TrackingService>.Instance);

    // ── Carrier detection ──

    [Theory]
    [InlineData("DHL: 1234567890", "1234567890", "DHL")]
    [InlineData("dhl:1234567890", "1234567890", "DHL")]
    [InlineData("  FedEx: 7777 8888 9999 ", "7777 8888 9999", "FedEx")]
    [InlineData("JMLA: ABC-1", "ABC-1", "JMLA")]
    public void ExplicitCarrierPrefix_IsTrusted(string text, string number, string carrier)
    {
        Assert.Equal((number, carrier), TrackingService.ExtractTracking(text));
    }

    [Theory]
    [InlineData("connote 123456789 sent", "123456789", "TNT")]
    [InlineData("12345678", "12345678", "Smartways")]
    [InlineData("ref 54321", "54321", "JMLA")]
    public void BareNumbers_AreGuessedByLength(string text, string number, string carrier)
    {
        Assert.Equal((number, carrier), TrackingService.ExtractTracking(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("DHL:")]
    [InlineData("no numbers here")]
    [InlineData("1234567890")]  // ten digits: no carrier uses that length without a prefix
    [InlineData("1234")]
    public void TextWithoutARecognisableNumber_FindsNothing(string? text)
    {
        Assert.Equal((null, null), TrackingService.ExtractTracking(text));
    }

    // ── Lookups and caching ──

    [Fact]
    public async Task DhlLookups_AreCached_EvenAcrossRestarts()
    {
        using var db = new TestDb();
        var dhl = new FakeCarrier("DHL");

        await Service(db, dhl).GetTrackingAsync("111", "DHL");
        await Service(db, dhl).GetTrackingAsync("111", "DHL"); // a new instance, as after an app restart

        Assert.Equal(1, dhl.Lookups);
        using var ctx = db.CreateDbContext();
        Assert.Equal("DHL:111", ctx.TrackingCache.Single().Key);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Rate Limited")]
    [InlineData("HTTP 500")]
    public async Task FailedLookups_AreNeverCached(string failureStatus)
    {
        using var db = new TestDb();
        var dhl = new FakeCarrier("DHL", failureStatus);
        var service = Service(db, dhl);

        await service.GetTrackingAsync("111", "DHL");
        await service.GetTrackingAsync("111", "DHL");

        Assert.Equal(2, dhl.Lookups);
    }

    [Fact]
    public async Task StaleCacheEntries_AreLookedUpAgain()
    {
        using var db = new TestDb();
        using (var ctx = db.CreateDbContext())
        {
            ctx.TrackingCache.Add(new TrackingCacheEntry { Key = "DHL:111", Carrier = "DHL", Status = "Old", FetchedUtc = DateTime.UtcNow.AddDays(-2) });
            ctx.SaveChanges();
        }
        var dhl = new FakeCarrier("DHL", "Delivered");

        var result = await Service(db, dhl).GetTrackingAsync("111", "DHL");

        Assert.Equal("Delivered", result.Status);
        Assert.Equal(1, dhl.Lookups);
    }

    [Fact]
    public async Task JmlaLookups_AreNotCachedHere()
    {
        using var db = new TestDb();
        var jmla = new FakeCarrier("JMLA");
        var service = Service(db, jmla);

        await service.GetTrackingAsync("54321", "JMLA");
        await service.GetTrackingAsync("54321", "JMLA");

        Assert.Equal(2, jmla.Lookups);
    }

    [Fact]
    public async Task UnknownCarrier_SaysSoInsteadOfFailing()
    {
        using var db = new TestDb();

        var result = await Service(db).GetTrackingAsync("111", "Pigeon");

        Assert.Equal("Unsupported carrier", result.Status);
    }

    [Fact]
    public async Task JobTracking_UsesTheLastConnoteWithANumber()
    {
        using var db = new TestDb();
        var tnt = new FakeCarrier("TNT");
        var dhl = new FakeCarrier("DHL");

        await Service(db, tnt, dhl).GetJobTrackingAsync(new[] { "TNT: 123456789", null, "DHL: 999", "no number" }, notes: null);

        Assert.Equal(new[] { "999" }, dhl.Numbers);
        Assert.Empty(tnt.Numbers);
    }

    [Fact]
    public async Task JobTracking_FallsBackToTheJobNotes()
    {
        using var db = new TestDb();
        var tnt = new FakeCarrier("TNT");

        await Service(db, tnt).GetJobTrackingAsync(new string?[] { null, "" }, notes: "Sent via TNT 123456789");

        Assert.Equal(new[] { "123456789" }, tnt.Numbers);
    }

    [Fact]
    public async Task JobTracking_WithNothingToTrack_ShowsADash()
    {
        using var db = new TestDb();

        var result = await Service(db).GetJobTrackingAsync(Array.Empty<string?>(), notes: "no number");

        Assert.Equal("—", result.Status);
    }
}
