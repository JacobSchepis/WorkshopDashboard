using Microsoft.Extensions.Options;

namespace WorkshopDashboard.Services.External.Jms;

/// <summary>Builds links into JMS for pages to render (e.g. a job number that opens the job
/// in JMS). No request is made — these are just hrefs built from JmsOptions. Injected
/// into every component via _Imports.razor as <c>JmsLinks</c>.</summary>
public sealed class JmsLinks
{
    private readonly string _base;
    private readonly JmsEndpoints _endpoints;

    public JmsLinks(IOptions<JmsOptions> options)
    {
        _base = string.IsNullOrWhiteSpace(options.Value.LinkBaseUrl)
            ? options.Value.BaseUri.ToString()
            : options.Value.LinkBaseUrl.TrimEnd('/') + "/";
        _endpoints = options.Value.Endpoints;
    }

    public string JobSummary(int? jobId) => JobSummary(jobId?.ToString());

    public string JobSummary(string? jobId) =>
        $"{_base}{_endpoints.JobSummary.TrimStart('/')}?id={Uri.EscapeDataString(jobId ?? "")}";
}
