using System.Text.Json;
using System.Text.Json.Serialization;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Tests.Jms;

// JmsWrite is what gets saved to the outbox table — it has to come back out exactly as it
// went in, whatever kind it is.
public class JmsWriteTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    private static JmsWrite RoundTrip(JmsWrite write) =>
        JsonSerializer.Deserialize<JmsWrite>(JsonSerializer.Serialize(write, Json), Json)!;

    [Fact]
    public void AddServiceLine_SurvivesSaving()
    {
        var write = (AddJobServiceWrite)JmsWrite.AddQuoteLine(1001, "Replace light guide", 1250.50m, "77");

        var back = Assert.IsType<AddJobServiceWrite>(RoundTrip(write));

        Assert.Equal(1001, back.JobId);
        Assert.Equal(write.Request.ServiceTypeId, back.Request.ServiceTypeId);
        Assert.Equal("Replace light guide", back.Request.Description);
        Assert.Equal(1250.50m, back.Request.CostPrice);
        Assert.Equal(125.05m, back.Request.TaxAmount);
        Assert.Equal("77", back.Request.EquipmentId);
    }

    [Fact]
    public void UpdateServiceLine_SurvivesSaving()
    {
        var write = new UpdateJobServiceWrite(1001, 55, new JobServiceUpdateRequest
        {
            ServiceTypeId = 11,
            Description = "ETA - 2026-10-01",
            UserId = 7,
            TechnicianName = "Alice",
            ItemHide = true
        });

        var back = Assert.IsType<UpdateJobServiceWrite>(RoundTrip(write));

        Assert.Equal((1001, 55), (back.JobId, back.ServiceId));
        Assert.Equal("ETA - 2026-10-01", back.Request.Description);
        Assert.Equal(7, back.Request.UserId);
        Assert.Equal("Alice", back.Request.TechnicianName);
        Assert.True(back.Request.ItemHide);
    }

    [Fact]
    public void DeleteServiceLine_SurvivesSaving()
    {
        Assert.Equal(new DeleteJobServiceWrite(1001, 55), RoundTrip(new DeleteJobServiceWrite(1001, 55)));
    }

    [Fact]
    public void StatusChange_SurvivesSaving()
    {
        var write = new ChangeJobStatusWrite(1001, JobStatus.LoanUnitProvided, "sent out", 48);
        Assert.Equal(write, RoundTrip(write));
    }

    [Fact]
    public void StatusChange_IsSavedByName_SoReorderingTheEnumCannotChangeIt()
    {
        var json = JsonSerializer.Serialize<JmsWrite>(new ChangeJobStatusWrite(1001, JobStatus.LoanUnitProvided), Json);
        Assert.Contains("\"LoanUnitProvided\"", json);
    }

    [Theory]
    [InlineData(JmsClient.ServiceTypeQuoted, "quote line")]
    [InlineData(JmsClient.ServiceTypeFreight, "freight line")]
    [InlineData(JmsClient.ServiceTypeNote, "note")]
    public void Description_NamesTheKindOfLine(int serviceTypeId, string expected)
    {
        var write = new AddJobServiceWrite(1001, JmsClient.BuildPricedRequest(serviceTypeId, "text", 0m, ""));
        Assert.Equal($"Add {expected} to job 1001: text", write.Describe());
    }

    [Fact]
    public void Description_ShortensLongText()
    {
        var description = JmsWrite.AddNote(1001, new string('x', 200)).Describe();
        Assert.True(description.Length < 120);
        Assert.EndsWith("...", description);
    }
}
