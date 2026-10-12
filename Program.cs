using System.Globalization;
using BlazorBootstrap;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpLogging;
using WorkshopDashboard;
using WorkshopDashboard.Components;
using WorkshopDashboard.Components.Helpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WorkshopDashboard.Services.Accounts;
using WorkshopDashboard.Abstract;
using WorkshopDashboard.Data;
using WorkshopDashboard.Services.Admin;
using WorkshopDashboard.Models;
using WorkshopDashboard.Services.Analytics;
using WorkshopDashboard.Demo;
using WorkshopDashboard.Services.External;
using WorkshopDashboard.Services.External.Email;
using WorkshopDashboard.Services.External.OpenAi;
using WorkshopDashboard.Services.External.Google;
using WorkshopDashboard.Services.External.Monday;
using WorkshopDashboard.Services.External.Jms;
using WorkshopDashboard.Services.External.Shipping;
using WorkshopDashboard.Services.Jobs;
using WorkshopDashboard.Services.Monday;
using WorkshopDashboard.Services.Quoting;
using WorkshopDashboard.Services.Quoting.Apex;
using WorkshopDashboard.Services.Reporting;
using WorkshopDashboard.Services.Shipping;
using WorkshopDashboard.Services.Stores;
using WorkshopDashboard.Services.Workshop;

// Pages format prices with bare .ToString("C"), which uses CultureInfo.CurrentCulture — that
// defaults to whatever the host machine's OS region is set to. On a machine regioned to
// English (UK) that renders "£" instead of "$", even though every price in this app is AUD/USD.
// Pinning it here fixes every "C"-formatted amount app-wide instead of threading an explicit
// culture through each page.
var appCulture = CultureInfo.GetCultureInfo("en-AU");
CultureInfo.DefaultThreadCurrentCulture = appCulture;
CultureInfo.DefaultThreadCurrentUICulture = appCulture;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Cookie login; the credentials themselves are checked against JMS (see UserAccountService).
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "auth_token";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
    });

// Every credential and external id (JMS, OpenAI, carriers, Google, monday.com) comes from
// configuration — see JmsOptions and ExternalOptions. Sources, each overriding the last:
// Secrets/secrets.json (gitignored — see Secrets.example/secrets.json), user secrets in
// Development, then environment variables (e.g. OpenAI__ApiKey), so a host like Azure can
// supply them as app settings with no file at all. Environment variables are re-added after
// the file because CreateBuilder already added them before it.
builder.Configuration.AddJsonFile(Path.Combine("Secrets", "secrets.json"), optional: true, reloadOnChange: false);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();

// Demo mode (on by default — see appsettings.json and Demo/DemoMode.cs) swaps JMS and every
// other outside service for in-process stand-ins over seeded data, so the app runs with no
// secrets at all. Set DemoMode=false (e.g. in Secrets/secrets.json) for the real thing.
var demoMode = builder.Configuration.GetValue<bool>(DemoMode.ConfigKey);

// JMS is validated at startup so a missing entry stops the app with a readable list instead
// of failing mid-form. The rest are optional — each service warns and reports itself
// unconfigured.
if (!demoMode)
{
    builder.Services.AddOptions<JmsOptions>()
        .Bind(builder.Configuration.GetSection(JmsOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<JmsOptions>, JmsOptionsValidator>();
}
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.Configure<DhlOptions>(builder.Configuration.GetSection(DhlOptions.SectionName));
builder.Services.Configure<FedExOptions>(builder.Configuration.GetSection(FedExOptions.SectionName));
builder.Services.Configure<JmlaOptions>(builder.Configuration.GetSection(JmlaOptions.SectionName));
builder.Services.Configure<MondayOptions>(builder.Configuration.GetSection(MondayOptions.SectionName));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));
DevOnlyComponentBase.AllowedUsername =
    builder.Configuration.GetSection(DevToolsOptions.SectionName).Get<DevToolsOptions>()?.AllowedUsername ?? "";

// Reads run as the shared service account; writes run as the logged-in user, whose JMS
// session lives in JmsUserSessionPool and is re-established from their saved password.
builder.Services.AddSingleton<JmsReadSession>();
builder.Services.AddSingleton<JmsUserSessionPool>();
builder.Services.AddSingleton<JmsLinks>();

// Writes that can wait (service lines, status changes) are queued in the database while JMS
// can't take them, and sent by the worker once it can — see JmsWriteQueue.
builder.Services.AddSingleton<JmsWriteQueue>();
builder.Services.AddHostedService<JmsWriteQueueWorker>();

builder.Services.AddSingleton<MonthlyReportBuilder>();
builder.Services.AddSingleton<MonthlyReportService>();
builder.Services.AddHostedService<MonthlyReportWorker>();

// App login (see UserAccountService): JMS decides whenever it's reachable; each user's
// password is saved hashed (offline login) and encrypted (silent JMS re-login).
builder.Services.AddSingleton<JmsCredentialStore>();
builder.Services.AddSingleton<UserAccountService>();

builder.Services.AddSingleton<AnalyticsWriter>();
builder.Services.AddSingleton<ActionTimeSavingStore>();
builder.Services.AddScoped<AnalyticsService>();

// BlazorBootstrap component services (ThemeSwitcher, Toasts, Modal, etc. all need this).
builder.Services.AddBlazorBootstrap();

// The app's own data lives in one SQLite file, App_Data/workshop.db (App_Data is
// gitignored, one database per environment; demo mode keeps its own, workshop-demo.db). Stores are singletons that open a short-lived
// context per call through this factory. See WorkshopDbContext.
var dataDir = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
Directory.CreateDirectory(dataDir);
builder.Services.AddDbContextFactory<WorkshopDbContext>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataDir, demoMode ? "workshop-demo.db" : "workshop.db")}"));

// Data Protection keys encrypt the auth cookie and the saved JMS passwords. Kept in
// App_Data/keys so they survive restarts and redeploys; on Windows they're further protected
// with DPAPI, which ties them to this machine. Lose or move them and everyone logs in again
// and re-enters their JMS password once — nothing worse.
var keyRing = builder.Services.AddDataProtection()
    .SetApplicationName("WorkshopDashboard")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) keyRing.ProtectKeysWithDpapi();

builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<CompanyGroupStore>();
builder.Services.AddSingleton<ManufacturerAbbreviationStore>();
builder.Services.AddSingleton<ApexPriceSheetStore>();
builder.Services.AddSingleton<ApexRepairSuggester>();
builder.Services.AddSingleton<ScopeJobStore>();
builder.Services.AddSingleton<HighlightedJobsStore>();
builder.Services.AddSingleton<ScopeCustomerStore>();
builder.Services.AddSingleton<RepairerStore>();
builder.Services.AddSingleton<CustomsInfoStore>();
builder.Services.AddSingleton<LoanEquipmentOverlayStore>();
builder.Services.AddSingleton<LoanEquipmentStatusStore>();
builder.Services.AddSingleton<LoanModelSubstituteStore>();
builder.Services.AddSingleton<DevicePairingStore>();
builder.Services.AddSingleton<TurnaroundStatStore>();
builder.Services.AddSingleton<TeamNoticeStore>();
builder.Services.AddSingleton<WorkshopTaskStore>();
builder.Services.AddSingleton<QuoteNoticeStore>();
builder.Services.AddSingleton<InterestedModelStore>();
builder.Services.AddSingleton<JobOfferOverlayStore>();
builder.Services.AddSingleton<IgnoredJobStatusStore>();
builder.Services.AddSingleton<TimesheetSettingsStore>();
builder.Services.AddSingleton<TimesheetTaskLogStore>();
builder.Services.AddSingleton<JmsUptimeStore>();
builder.Services.AddSingleton<GeneratedDocumentStore>();
// Scoped (not Singleton) — looks up who's logged in on this circuit and fetches their JMS
// session from JmsUserSessionPool, for writes. Services depend on this (and JmsReadSession
// for reads) instead of pages holding their own client (see JmsComponentBase).
builder.Services.AddScoped<JmsUserSession>();

// Scoped (not Singleton) — JobNotesService depends on CurrentUserResolver, which
// depends on the scoped AuthenticationStateProvider.
builder.Services.AddScoped<CurrentUserResolver>();
builder.Services.AddScoped<JobNotesService>();
builder.Services.AddScoped<TimeSheetService>();
builder.Services.AddScoped<TimesheetLoggingService>();
builder.Services.AddScoped<JobServiceLookup>();
builder.Services.AddScoped<JobEquipmentLookup>();
builder.Services.AddScoped<JobTouchService>();
builder.Services.AddScoped<WorkshopQueueLookup>();
builder.Services.AddScoped<WaitingQuoteConfirmationService>();
builder.Services.AddScoped<WaitingWorkshopProcessingService>();
builder.Services.AddScoped<JobLookupService>();
builder.Services.AddScoped<QuoteSubmissionService>();
builder.Services.AddScoped<WaitingQuoteJobLookup>();
builder.Services.AddScoped<ShippingJobLookup>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<InspectionDocumentBuilder>();
builder.Services.AddScoped<InspectionReportLookup>();
builder.Services.AddScoped<DashboardLookup>();
builder.Services.AddScoped<MondayPurchasingService>();
builder.Services.AddScoped<ScopeLookupService>();
builder.Services.AddScoped<RepairerAssignmentLookup>();
builder.Services.AddScoped<ModelWatchlistLookup>();
builder.Services.AddScoped<LoanEquipmentLookup>();
builder.Services.AddScoped<ScopeReportLookup>();
builder.Services.AddScoped<JobStatusActionService>();
builder.Services.AddScoped<RecallLookup>();
builder.Services.AddScoped<BulkJobCreationService>();
builder.Services.AddSingleton<WaitingQuoteEmailConfig>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddSingleton<TrackingService>();

if (demoMode)
{
    builder.Services.AddDemoServices();
}
else
{
    builder.Services.AddSingleton(JmsTransport.Network);
    builder.Services.AddSingleton<IJsonCompletionClient, OpenAiQuoteClient>();
    builder.Services.AddSingleton<GoogleAuthorizer>();
    builder.Services.AddSingleton<IMailTransport, GmailMailTransport>();
    builder.Services.AddSingleton<ICustomerCreditSource, CustomerCreditLookup>();
    builder.Services.AddSingleton<ILoanEquipmentExporter, LoanEquipmentSheetExportService>();
    builder.Services.AddHttpClient<IMondayClient, MondayService>();

    builder.Services.AddHttpClient<TntTrackingService>();
    builder.Services.AddHttpClient<JmlaTrackingService>();

    // Every carrier client is registered as a plain ICarrierTracker — no per-carrier caching
    // wrapper. Caching (for the carriers that need it) lives once, in TrackingService itself.
    builder.Services.AddSingleton<ICarrierTracker, TntTrackingService>();
    builder.Services.AddSingleton<ICarrierTracker>(sp => sp.GetRequiredService<JmlaTrackingService>());

    builder.Services.AddHttpClient<DhlTrackingService>();
    builder.Services.AddSingleton<ICarrierTracker, DhlTrackingService>();

    builder.Services.AddHttpClient<FedExTrackingService>();
    builder.Services.AddSingleton<ICarrierTracker, FedExTrackingService>();

    // Background service — no JMS login, just a plain periodic HTTP reachability check of
    // the configured base URL. See JmsUptimeMonitorService's header comment.
    builder.Services.AddSingleton<JmsStatus>();
    builder.Services.AddHttpClient<JmsUptimeMonitorService>();
    builder.Services.AddHostedService<JmsUptimeMonitorService>();
}

builder.Services.AddAuthorization();

builder.Services.AddHttpContextAccessor();

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

// Bring the database schema up to date before the first request.
using (var db = app.Services.GetRequiredService<IDbContextFactory<WorkshopDbContext>>().CreateDbContext())
    db.Database.Migrate();
if (demoMode) await app.SeedDemoAppDataAsync();

app.UseSession();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStaticFiles();

app.UseAntiforgery();

// Generated PDFs (Service Report) — served over plain HTTP rather than pushed through
// the Blazor circuit. ?download=true sends it as an attachment instead of inline.
// Both downloads are counted here rather than from the page's link, so a ctrl-click, new tab
// or bookmark still shows up in Usage Analytics.
app.MapGet("/generated-documents/{id:guid}", async (Guid id, bool? download, GeneratedDocumentStore store, AnalyticsWriter analytics, HttpContext http) =>
{
    var doc = store.Get(id);
    if (doc == null) return Results.NotFound();
    await analytics.AppendAsync(new AnalyticsEvent
    {
        EventType = UsageSummaries.Action, Name = "Service report PDF downloaded",
        Detail = doc.Value.FileName, Username = http.User.Identity?.Name,
    });
    return download == true
        ? Results.File(doc.Value.Bytes, "application/pdf", doc.Value.FileName)
        : Results.File(doc.Value.Bytes, "application/pdf");
}).RequireAuthorization();

// Finished monthly site report packs, straight off disk (App_Data/monthly-reports).
app.MapGet("/monthly-reports/{id:int}", async (int id, MonthlyReportService reports, AnalyticsWriter analytics, HttpContext http) =>
{
    var run = await reports.GetRunAsync(id);
    var path = run == null ? null : reports.GetFilePath(run);
    if (path == null) return Results.NotFound();
    await analytics.AppendAsync(new AnalyticsEvent
    {
        EventType = UsageSummaries.Action, Name = "Monthly report pack downloaded",
        Detail = $"{run!.CompanyName} ({run.Month})", Username = http.User.Identity?.Name,
    });
    return Results.File(path, "application/pdf", Path.GetFileName(path));
}).RequireAuthorization();

if (demoMode) app.MapDemoJmsPages();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
