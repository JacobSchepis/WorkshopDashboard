using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WorkshopDashboard.Models;

namespace WorkshopDashboard.Data;

// ═══════════════════════════════════════════════════════════════════════════
//  WorkshopDbContext
//
//  The app's own data — everything that used to be a JSON file in App_Data —
//  in one SQLite database (App_Data/workshop.db). Stores stay singletons and
//  create a short-lived context per call via IDbContextFactory, so pages and
//  services don't know the storage changed.
//
//  Name-keyed records (customers, repairers, manufacturers, 510(k)s, loan
//  statuses) keep a hidden integer Id so a rename is a plain update, and
//  their name columns are NOCASE-collated with a unique index — the same
//  case-insensitive uniqueness the stores used to enforce by hand.
//
//  Schema changes go through EF migrations (src/Data/Migrations), applied
//  automatically at startup.
// ═══════════════════════════════════════════════════════════════════════════

public class WorkshopDbContext : DbContext
{
    public const string PositionProperty = "Position";

    public WorkshopDbContext(DbContextOptions<WorkshopDbContext> options) : base(options) { }

    /// <summary>Adds a row that keeps a hidden Position (see OnModelCreating), placing it last.</summary>
    public async Task AddLastAsync<T>(T entity) where T : class
    {
        var set = Set<T>();
        var last = await set.MaxAsync(x => (int?)EF.Property<int>(x, PositionProperty)) ?? -1;
        set.Add(entity);
        Entry(entity).Property(PositionProperty).CurrentValue = last + 1;
    }

    /// <summary>Rows that keep a hidden Position, in that order.</summary>
    public IQueryable<T> InPositionOrder<T>() where T : class =>
        Set<T>().OrderBy(x => EF.Property<int>(x, PositionProperty));

    // ── Scope jobs / Overview / Model Watchlist ──
    public DbSet<ScopeExclusion> ScopeExclusions => Set<ScopeExclusion>();
    public DbSet<HighlightedJob> HighlightedJobs => Set<HighlightedJob>();
    public DbSet<InterestedModel> InterestedModels => Set<InterestedModel>();
    public DbSet<IgnoredJobStatus> IgnoredJobStatuses => Set<IgnoredJobStatus>();
    public DbSet<JobOfferOverlay> JobOfferOverlays => Set<JobOfferOverlay>();

    // ── Customers / repairers ──
    public DbSet<ScopeCustomer> ScopeCustomers => Set<ScopeCustomer>();
    public DbSet<Repairer> Repairers => Set<Repairer>();

    // ── Quoting ──
    public DbSet<QuoteNotice> QuoteNotices => Set<QuoteNotice>();
    public DbSet<CompanyGroupOverride> CompanyGroupOverrides => Set<CompanyGroupOverride>();
    public DbSet<ManufacturerAbbreviation> ManufacturerAbbreviations => Set<ManufacturerAbbreviation>();

    // ── Loan equipment ──
    public DbSet<LoanEquipmentOverlay> LoanEquipmentOverlays => Set<LoanEquipmentOverlay>();
    public DbSet<LoanEquipmentStatusOption> LoanEquipmentStatuses => Set<LoanEquipmentStatusOption>();
    public DbSet<LoanModelSubstitutePair> LoanModelSubstitutes => Set<LoanModelSubstitutePair>();

    // ── Customs ──
    public DbSet<CustomsManufacturer> CustomsManufacturers => Set<CustomsManufacturer>();
    public DbSet<CustomsFda510k> CustomsFda510ks => Set<CustomsFda510k>();
    public DbSet<CustomsModel> CustomsModels => Set<CustomsModel>();

    // ── Workshop screens ──
    public DbSet<TeamNotice> TeamNotices => Set<TeamNotice>();
    public DbSet<WorkshopTask> WorkshopTasks => Set<WorkshopTask>();
    public DbSet<TurnaroundStat> TurnaroundStats => Set<TurnaroundStat>();

    // ── Timesheets / settings / monitoring / caches ──
    public DbSet<TimesheetTaskLogEntry> TimesheetTaskLog => Set<TimesheetTaskLogEntry>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<JmsOutagePeriod> JmsOutages => Set<JmsOutagePeriod>();
    public DbSet<TrackingCacheEntry> TrackingCache => Set<TrackingCacheEntry>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<JmsOutboxItem> JmsOutbox => Set<JmsOutboxItem>();

    public DbSet<MonthlyReportSite> MonthlyReportSites => Set<MonthlyReportSite>();
    public DbSet<MonthlyReportRun> MonthlyReportRuns => Set<MonthlyReportRun>();
    public DbSet<ApexPriceLine> ApexPriceLines => Set<ApexPriceLine>();

    public DbSet<ActionTimeSaving> ActionTimeSavings => Set<ActionTimeSaving>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ScopeExclusion>(e =>
        {
            e.HasKey(x => new { x.Kind, x.Id });
            e.Property(x => x.Kind).HasConversion<string>();
        });
        b.Entity<HighlightedJob>().HasKey(x => x.JobId);
        b.Entity<InterestedModel>().HasKey(x => x.ModelId);
        b.Entity<IgnoredJobStatus>().HasKey(x => x.JobStatusId);
        b.Entity<JobOfferOverlay>().HasKey(x => x.JobId);

        b.Entity<ScopeCustomer>(e =>
        {
            e.Property<int>("Id");
            e.HasKey("Id");
            e.Property(x => x.DisplayName).UseCollation("NOCASE");
            e.HasIndex(x => x.DisplayName).IsUnique();
            e.Property(x => x.Departments).HasConversion(JsonConverter<List<ScopeDepartment>>(), JsonComparer<List<ScopeDepartment>>());
        });

        b.Entity<Repairer>(e =>
        {
            e.Property<int>("Id");
            e.HasKey("Id");
            e.Property(x => x.Name).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<QuoteNotice>(e =>
        {
            e.HasKey(x => x.Id);
            // Enum names, not ordinals, so reordering QuoteNoticeTarget can't reassign notices.
            e.PrimitiveCollection(x => x.Targets).ElementType(t => t.HasConversion<string>());
        });
        b.Entity<CompanyGroupOverride>().HasKey(x => new { x.Scope, x.CompanyId });
        b.Entity<ManufacturerAbbreviation>().HasKey(x => x.ManufacturerCompanyId);

        b.Entity<LoanEquipmentOverlay>().HasKey(x => x.EquipmentId);
        b.Entity<LoanEquipmentStatusOption>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Label).UseCollation("NOCASE");
            e.HasIndex(x => x.Label).IsUnique();
        });
        b.Entity<LoanModelSubstitutePair>().HasKey(x => new { x.LoanModelId, x.SubstituteModelId });

        b.Entity<CustomsManufacturer>(e =>
        {
            e.Property<int>("Id");
            e.HasKey("Id");
            e.Property(x => x.Name).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<CustomsFda510k>(e =>
        {
            e.Property<int>("Id");
            e.HasKey("Id");
            e.Property(x => x.Number).UseCollation("NOCASE");
            e.HasIndex(x => x.Number).IsUnique();
        });
        b.Entity<CustomsModel>(e =>
        {
            e.HasKey(x => x.ModelId);
            e.Property(x => x.ModelId).ValueGeneratedNever();
            e.Property(x => x.ManufacturerName).UseCollation("NOCASE");
            e.Property(x => x.Fda510kNumber).UseCollation("NOCASE");
        });

        b.Entity<TeamNotice>().HasKey(x => x.Id);
        // These two used to be shown in file order, which a table doesn't have — Position
        // keeps it (new rows go last).
        b.Entity<WorkshopTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Frequency).HasConversion<string>();
            e.Property<int>(PositionProperty);
        });
        b.Entity<TurnaroundStat>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property<int>(PositionProperty);
        });

        b.Entity<TimesheetTaskLogEntry>().HasKey(x => new { x.TechnicianUserId, x.TaskKey, x.Date });
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<JmsOutagePeriod>(e =>
        {
            e.Property<int>("Id");
            e.HasKey("Id");
        });
        b.Entity<TrackingCacheEntry>().HasKey(x => x.Key);

        b.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Username).UseCollation("NOCASE");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<JmsOutboxItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Username).UseCollation("NOCASE");
            e.Property(x => x.State).HasConversion<string>();
            e.HasIndex(x => new { x.State, x.Username });
        });

        b.Entity<ActionTimeSaving>(e =>
        {
            e.HasKey(x => x.ActionName);
            e.Property(x => x.ActionName).UseCollation("NOCASE");
        });

        b.Entity<ApexPriceLine>(e =>
        {
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).UseCollation("NOCASE");
            e.Property(x => x.PriceUsd).HasConversion<double>();
        });

        b.Entity<MonthlyReportSite>(e =>
        {
            e.HasKey(x => x.CompanyId);
            e.Property(x => x.CompanyName).UseCollation("NOCASE");
        });

        b.Entity<MonthlyReportRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.State).HasConversion<string>();
            e.HasIndex(x => new { x.Month, x.CompanyId }).IsUnique();
            e.HasIndex(x => x.State);
        });

        // Every other single int key is a real JMS id, never generated here.
        var generatedKeys = new[] { typeof(AppUser), typeof(JmsOutboxItem), typeof(MonthlyReportRun) };
        foreach (var entity in b.Model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey();
            if (!generatedKeys.Contains(entity.ClrType) && key is { Properties.Count: 1 } && !key.Properties[0].IsShadowProperty()
                && key.Properties[0].ClrType == typeof(int))
                key.Properties[0].ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }
    }

    // Stores a nested object graph as one JSON text column. Compared by serialized value so
    // EF notices in-place edits to the list.
    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<T, string> JsonConverter<T>() where T : new() =>
        new(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<T>(v, (JsonSerializerOptions?)null) ?? new T());

    private static ValueComparer<T> JsonComparer<T>() where T : new() =>
        new((a, b) => JsonSerializer.Serialize(a, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(b, (JsonSerializerOptions?)null),
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null).GetHashCode(),
            v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null) ?? new T());
}
