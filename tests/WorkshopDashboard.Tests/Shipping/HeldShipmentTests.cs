using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Shipping;

namespace WorkshopDashboard.Tests.Shipping;

// What the kiosk's "On hold" column counts as held: courier holds and exceptions, in whatever
// words each courier uses — not normal movement, not delivered, not our own lookup errors.
public class HeldShipmentTests
{
    [Theory]
    [InlineData("Shipment on hold")]
    [InlineData("Shipment is on hold - Awaiting Clearance")]
    [InlineData("Held at customs")]
    [InlineData("Delivery Exception")]
    [InlineData("Exception")]
    [InlineData("Delivery Attempted")]
    [InlineData("Delivery attempt failed - recipient not available")]
    [InlineData("Clearance failure")]
    public void HoldsAndExceptions_AreHeld(string status)
    {
        Assert.True(HeldShipmentLookup.IsHeld(new TrackingResult { Status = status }));
    }

    [Theory]
    [InlineData("In Transit")]
    [InlineData("Booked")]
    [InlineData("Out for Delivery")]
    [InlineData("Arrived at Sort Facility SYDNEY - AUSTRALIA")]
    [InlineData("Picked Up")]
    [InlineData("Error")]
    [InlineData("Not found")]
    [InlineData("Unauthorized")]
    [InlineData("Unknown")]
    [InlineData("—")]
    public void NormalMovement_AndLookupProblems_AreNotHeld(string status)
    {
        Assert.False(HeldShipmentLookup.IsHeld(new TrackingResult { Status = status }));
    }

    [Fact]
    public void Delivered_IsNeverHeld_EvenAfterAnEarlierException()
    {
        Assert.False(HeldShipmentLookup.IsHeld(new TrackingResult { Status = "Delivered after delivery exception", IsDelivered = true }));
    }
}
