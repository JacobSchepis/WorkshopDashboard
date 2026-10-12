using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WorkshopDashboard.Services.External;
using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.External.Jms;

namespace WorkshopDashboard.Tests.Admin;

// Settings come from configuration (Secrets/secrets.json, user secrets, environment variables).
// Missing optional settings fail only the feature that needs them; the example file stays in
// step with the options classes it documents.
public class ConfigurationTests
{
    [Fact]
    public async Task OpenAi_WithoutAKey_FailsOnlyWhenUsed()
    {
        var client = new OpenAiQuoteClient(Options.Create(new OpenAiOptions()));

        Assert.False(client.IsConfigured);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteJsonSchemaAsync("system", "text", "user", "schema", "{}"));
        Assert.Contains("OpenAI:ApiKey", ex.Message);
    }

    [Fact]
    public void Sections_BindToTheirOptions()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FedEx:ClientId"] = "id",
            ["FedEx:UseSandbox"] = "false",
            ["Google:LoanEquipmentTabGid"] = "42",
        }).Build();

        var fedEx = config.GetSection(FedExOptions.SectionName).Get<FedExOptions>()!;
        var google = config.GetSection(GoogleOptions.SectionName).Get<GoogleOptions>()!;

        Assert.Equal("id", fedEx.ClientId);
        Assert.False(fedEx.UseSandbox);
        Assert.Equal(42, google.LoanEquipmentTabGid);
        Assert.Equal("Secrets", google.TokenStorePath); // default kept when not set
    }

    [Fact]
    public void LaterSources_OverrideTheSecretsFile()
    {
        // Same order Program.cs adds them: the file first, environment variables last.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "from-file" })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = "from-env" })
            .Build();

        Assert.Equal("from-env", config.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>()!.ApiKey);
    }

    [Fact]
    public void ExampleSecretsFile_ListsEverySetting()
    {
        var example = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepoRoot(), "Secrets.example", "secrets.json"))
            .Build();

        var missing = new List<string>();
        void Check(string section, Type options)
        {
            foreach (var p in options.GetProperties().Where(p => p.CanWrite))
            {
                if (p.PropertyType.IsClass && p.PropertyType != typeof(string))
                    Check($"{section}:{p.Name}", p.PropertyType);
                else if (example[$"{section}:{p.Name}"] == null)
                    missing.Add($"{section}:{p.Name}");
            }
        }

        Check(JmsOptions.SectionName, typeof(JmsOptions));
        Check(OpenAiOptions.SectionName, typeof(OpenAiOptions));
        Check(DhlOptions.SectionName, typeof(DhlOptions));
        Check(FedExOptions.SectionName, typeof(FedExOptions));
        Check(JmlaOptions.SectionName, typeof(JmlaOptions));
        Check(MondayOptions.SectionName, typeof(MondayOptions));
        Check(GoogleOptions.SectionName, typeof(GoogleOptions));
        Check(DevToolsOptions.SectionName, typeof(DevToolsOptions));

        // Defaults that aren't worth a line in the example (LinkBaseUrl is only for demo mode).
        missing.Remove("Jms:RequestTimeoutSeconds");
        missing.Remove("Jms:LinkBaseUrl");
        Assert.Empty(missing);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WorkshopDashboard.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Couldn't find the repo root above the test output.");
    }
}
