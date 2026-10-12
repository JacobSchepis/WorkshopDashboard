using System.Collections.Concurrent;

namespace WorkshopDashboard.Services.Stores;

/// <summary>
/// Short-lived in-memory holding area for generated PDFs, served over plain HTTP by the
/// /generated-documents/{id} endpoint in Program.cs. Large PDFs pushed to the browser as
/// base64 over the Blazor circuit stall on slower links (the Pi deployment); a normal
/// HTTP download doesn't. Entries expire after <see cref="Lifetime"/>.
/// </summary>
public class GeneratedDocumentStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private record Entry(byte[] Bytes, string FileName, DateTime ExpiresUtc);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public Guid Add(byte[] bytes, string fileName)
    {
        RemoveExpired();

        var id = Guid.NewGuid();
        _entries[id] = new Entry(bytes, fileName, DateTime.UtcNow + Lifetime);
        return id;
    }

    public (byte[] Bytes, string FileName)? Get(Guid id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return null;
        if (entry.ExpiresUtc < DateTime.UtcNow)
        {
            _entries.TryRemove(id, out _);
            return null;
        }
        return (entry.Bytes, entry.FileName);
    }

    public void Remove(Guid id) => _entries.TryRemove(id, out _);

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _entries)
            if (entry.ExpiresUtc < now)
                _entries.TryRemove(id, out _);
    }
}
