namespace WorkshopDashboard.Models;

public sealed class JobRecord
{
    public int JobId { get; set; }
    public string Owner { get; set; } = "";
    public string Model { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime StatusChangeDate { get; set; }
    public List<string> Connote { get; set; } = new();
    public bool IsSelected { get; set; } // checkbox state

    public override string ToString()
    {
        return $"JobId: {JobId}, Owner: {Owner}, Model: {Model}, StatusChangeDate: {StatusChangeDate}, ConnoteCount: {Connote.Count}";
    }
}