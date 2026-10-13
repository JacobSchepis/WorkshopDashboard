namespace WorkshopDashboard.Data;

/// <summary>
/// Where the app keeps its own files: the SQLite database, Data Protection keys, analytics and
/// monthly report packs. App_Data under the content root, unless the DataDirectory setting
/// says otherwise; on Azure App Service it points at /home/data, which outlives redeploys
/// (the deployed app folder may be read-only).
/// </summary>
public sealed class AppDataDirectory(string path)
{
    public string Path { get; } = path;

    public static AppDataDirectory From(IConfiguration config, string contentRoot) =>
        new(config["DataDirectory"] is { Length: > 0 } dir ? dir : System.IO.Path.Combine(contentRoot, "App_Data"));
}
