using CsvHelper;
using System.Globalization;
using WorkshopDashboard.Services.Shipping;
using WorkshopDashboard.Services.External.Shipping;
using WorkshopDashboard.Services.Workshop;

using WorkshopDashboard.Services.External.Jms;
namespace WorkshopDashboard.Services.Jobs;

public record JobServiceInfo(
    int ServiceId,
    int ServiceTypeId,
    string ServiceTypeName,
    string Description,
    decimal CostPrice,
    int Quantity,
    DateTime CreationDate);

/// <summary>
/// Loads the services already on a job — used by QuoteDorado.razor's "Services in Job" list
/// and its "Apply Credit" picker. Parsed with CsvHelper (rather than a plain Split(',')
/// like JobEquipmentLookup) because service.description is free text and can itself
/// contain commas/newlines.
/// </summary>
public class JobServiceLookup
{
    private readonly JmsReadSession _jmsRead;

    public JobServiceLookup(JmsReadSession jmsRead) => _jmsRead = jmsRead;

    public async Task<List<JobServiceInfo>> LoadAsync(int jobId)
    {
        var jms = await _jmsRead.GetClientAsync();
        if (jms == null) return new();

        var query = JmsQueries.JobServicesQuery(jobId);
        var csv = await jms.PostQueryForCsvAsync(query);

        var config = WaitingQuoteReport.MakeCsvConfig();
        using var reader = new StringReader(csv.Content);
        using var csvReader = new CsvReader(reader, config);

        return csvReader.GetRecords<JobServiceCsvRow>()
            .Select(r => new JobServiceInfo(
                r.service_id,
                r.service_type_id,
                r.service_type_name ?? "",
                r.description ?? "",
                r.cost_price,
                r.quantity,
                DateTime.TryParse(r.creation_date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.MinValue))
            .ToList();
    }

    private class JobServiceCsvRow
    {
        public int service_id { get; set; }
        public int service_type_id { get; set; }
        public string? service_type_name { get; set; }
        public string? description { get; set; }
        public decimal cost_price { get; set; }
        public int quantity { get; set; }
        public string? creation_date { get; set; }
    }
}
