using System.Text.RegularExpressions;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Workshop;

namespace WorkshopDashboard.Services.Shipping;

public record HeldShipment(string JobId, string Carrier, string Status, DateTime? LastEventTime);

/// <summary>
/// The "On hold" column of the WorkshopNotifications kiosk: every shipment the app tracks,
/// in either direction, that the courier currently reports as held or as an exception —
/// the ones someone needs to chase. Sources are the same job queues whose pages already show
/// live tracking:
///   Waiting Workshop Assignment, Workshop Finalisation Complete (tracking in job services),
///   Waiting Arrival At Workshop, Waiting Outward Shipping, Unit Departed Workshop (status-history connotes).
/// Shipments with no tracking, delivered ones, and anything the courier just reports as
/// moving normally are left out.
///
/// Static — this only runs from the kiosk page, which has its own IJmsReader
/// (KioskPairingComponentBase) rather than DI-injecting one.
/// </summary>
public static class HeldShipmentLookup
{
    // Each courier words these differently ("Shipment on hold", "Delivery Exception",
    // "Delivery Attempted", DHL's failure descriptions...), so match the words rather than
    // a per-courier code list.
    private static readonly Regex HeldWords = new(
        @"\b(on hold|hold|held|exception|attempt(ed)?|fail(ed|ure)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>True when the courier says the shipment is held or has hit an exception.</summary>
    public static bool IsHeld(TrackingResult result) =>
        !result.IsDelivered && HeldWords.IsMatch(result.Status ?? "");

    public static async Task<List<HeldShipment>> RunAsync(IJmsReader jms, TrackingService tracking, ScopeJobExclusions exclusions)
    {
        await tracking.PrimeAllAsync();

        // Tracking kept in job services (free text) vs. in status-history notes (connotes).
        var serviceTracked = await Task.WhenAll(
            WorkshopQueueLookup.GetWaitingWorkshopAssignmentJobsAsync(jms, exclusions),
            WorkshopQueueLookup.GetWorkshopFinalisationCompleteJobsAsync(jms, exclusions));
        var connoteTracked = await Task.WhenAll(
            ShippingJobLookup.GetJobsAsync(jms, (int)JobStatus.WaitingForArrivalAtWorkshop, filterHistoryToStatus: true, connoteStatusLabel: "Waiting for arrival at Workshop"),
            ShippingJobLookup.GetJobsAsync(jms, (int)JobStatus.WaitingOutwardShipping, filterHistoryToStatus: true, connoteStatusLabel: "Waiting Outward Shipping"),
            ShippingJobLookup.GetJobsAsync(jms, (int)JobStatus.UnitDepartedWorkshop, filterHistoryToStatus: false, connoteStatusLabel: "Waiting Outward Shipping"));

        var lookups = serviceTracked.SelectMany(jobs => jobs).Select(async j =>
            {
                var (number, carrier) = TrackingService.ExtractTracking(j.TrackingRaw);
                var result = number is null || carrier is null ? null : await tracking.GetTrackingAsync(number, carrier);
                return (j.JobId, Result: result);
            })
            .Concat(connoteTracked.SelectMany(jobs => jobs).Select(async j =>
            {
                var result = j.Connote.Count == 0 && string.IsNullOrWhiteSpace(j.Notes)
                    ? null
                    : await tracking.GetJobTrackingAsync(j.Connote, j.Notes);
                return (JobId: j.JobId.ToString(), Result: result);
            }));

        var results = await Task.WhenAll(lookups);

        // Longest-held first: the oldest courier update is the one that's been stuck longest.
        return results
            .Where(r => r.Result != null && IsHeld(r.Result))
            .DistinctBy(r => r.JobId)
            .Select(r => new HeldShipment(r.JobId, r.Result!.Carrier, r.Result.Status, r.Result.LastEventTime))
            .OrderBy(s => s.LastEventTime ?? DateTime.MaxValue)
            .ToList();
    }
}
