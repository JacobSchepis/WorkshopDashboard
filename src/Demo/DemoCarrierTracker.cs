using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Shipping;

namespace WorkshopDashboard.Demo;

/// <summary>Demo mode's stand-in for one carrier's tracking API. The answer is worked out from
/// the tracking number itself, so a given consignment always tells the same story — mostly in
/// transit or delivered, with the odd exception or attempted delivery for the badges to show.</summary>
public sealed class DemoCarrierTracker(string carrier) : ICarrierTracker
{
    private static readonly (string Status, string Event)[] Stages =
    [
        ("Booked", "Shipment information received"),
        ("In Transit", "Departed facility"),
        ("In Transit", "Arrived at sort facility"),
        ("Out for Delivery", "With delivery courier"),
        ("Delivered", "Delivered — signed for"),
        ("Delivered", "Delivered — signed for"),
        ("Delivered", "Delivered — left at reception"),
        ("Delivery Attempted", "Delivery attempted — business closed"),
        ("Delivery Exception", "Address query — held at depot"),
    ];

    private static readonly string[] Places = ["Sydney NSW", "Melbourne VIC", "Brisbane QLD", "Adelaide SA", "Perth WA", "Newcastle NSW"];

    public string Carrier => carrier;

    public Task PrimeAsync() => Task.CompletedTask;

    public Task<TrackingResult> GetTrackingAsync(string trackingNumber)
    {
        var digits = new string(trackingNumber.Where(char.IsDigit).ToArray());
        var seed = digits.Length == 0 ? 0 : (int)(long.Parse(digits[^Math.Min(9, digits.Length)..]) % 1000);
        var (status, lastEvent) = Stages[seed % Stages.Length];
        var daysAgo = seed % 9;

        return Task.FromResult(new TrackingResult
        {
            TrackingNumber = trackingNumber,
            Carrier = carrier,
            Status = status,
            LastEvent = lastEvent,
            LastLocation = Places[seed % Places.Length],
            LastEventTime = DateTime.Now.AddDays(-daysAgo).AddHours(-(seed % 7)),
            FirstEventTime = DateTime.Now.AddDays(-daysAgo - 2 - seed % 3),
            IsDelivered = status == "Delivered",
        });
    }
}
