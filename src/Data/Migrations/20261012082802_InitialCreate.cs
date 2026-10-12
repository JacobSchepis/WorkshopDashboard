using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkshopDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActionTimeSavings",
                columns: table => new
                {
                    ActionName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Minutes = table.Column<double>(type: "REAL", nullable: false),
                    PerItem = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionTimeSavings", x => x.ActionName);
                });

            migrationBuilder.CreateTable(
                name: "ApexPriceLines",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    LineItem = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    PriceUsd = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApexPriceLines", x => x.Code);
                });

            migrationBuilder.CreateTable(
                name: "CompanyGroupOverrides",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyGroupOverrides", x => new { x.Scope, x.CompanyId });
                });

            migrationBuilder.CreateTable(
                name: "CustomsFda510ks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Number = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ProductCode = table.Column<string>(type: "TEXT", nullable: false),
                    RegulationNumber = table.Column<string>(type: "TEXT", nullable: false),
                    PageUrl = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsFda510ks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomsManufacturers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Address = table.Column<string>(type: "TEXT", nullable: false),
                    RegistrationNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Country = table.Column<string>(type: "TEXT", nullable: false),
                    FdaEstablishmentPageUrl = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsManufacturers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomsModels",
                columns: table => new
                {
                    ModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelLabel = table.Column<string>(type: "TEXT", nullable: false),
                    ManufacturerName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Fda510kNumber = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    HtsCode = table.Column<string>(type: "TEXT", nullable: false),
                    DeclaredValue = table.Column<string>(type: "TEXT", nullable: false),
                    Weight = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceListingNumber = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomsModels", x => x.ModelId);
                });

            migrationBuilder.CreateTable(
                name: "HighlightedJobs",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightedJobs", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "IgnoredJobStatuses",
                columns: table => new
                {
                    JobStatusId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoredJobStatuses", x => x.JobStatusId);
                });

            migrationBuilder.CreateTable(
                name: "InterestedModels",
                columns: table => new
                {
                    ModelId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterestedModels", x => x.ModelId);
                });

            migrationBuilder.CreateTable(
                name: "JobOfferOverlays",
                columns: table => new
                {
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    RepairCostEstimate = table.Column<decimal>(type: "TEXT", nullable: true),
                    OfferEstimate = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobOfferOverlays", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "LoanEquipmentOverlays",
                columns: table => new
                {
                    EquipmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentLocationCompanyId = table.Column<int>(type: "INTEGER", nullable: true),
                    LoanedToCompanyId = table.Column<int>(type: "INTEGER", nullable: true),
                    OriginalOwnerCompanyId = table.Column<int>(type: "INTEGER", nullable: true),
                    BuyPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CoveringForJobIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanEquipmentOverlays", x => x.EquipmentId);
                });

            migrationBuilder.CreateTable(
                name: "LoanEquipmentStatuses",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Color = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanEquipmentStatuses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoanModelSubstitutes",
                columns: table => new
                {
                    LoanModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubstituteModelId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanModelSubstitutes", x => new { x.LoanModelId, x.SubstituteModelId });
                });

            migrationBuilder.CreateTable(
                name: "ManufacturerAbbreviations",
                columns: table => new
                {
                    ManufacturerCompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    Abbreviation = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturerAbbreviations", x => x.ManufacturerCompanyId);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyReportRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Month = table.Column<string>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Step = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    FilePath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyReportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MonthlyReportSites",
                columns: table => new
                {
                    CompanyId = table.Column<int>(type: "INTEGER", nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyReportSites", x => x.CompanyId);
                });

            migrationBuilder.CreateTable(
                name: "QuoteNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    IsGlobal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Targets = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteNotices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repairers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    ModelIds = table.Column<string>(type: "TEXT", nullable: false),
                    JobIds = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repairers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JmsOutages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JmsOutages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JmsOutbox",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    WriteJson = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NeedsCheck = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JmsOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScopeCustomers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    CompanyIds = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: true),
                    PrimaryContactIds = table.Column<string>(type: "TEXT", nullable: false),
                    SecondaryContactIds = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    CreditSheetTabName = table.Column<string>(type: "TEXT", nullable: true),
                    Departments = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopeCustomers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScopeExclusions",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScopeExclusions", x => new { x.Kind, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TeamNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamNotices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TimesheetTaskLog",
                columns: table => new
                {
                    TechnicianUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    TaskKey = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimesheetTaskLog", x => new { x.TechnicianUserId, x.TaskKey, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "TrackingCache",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    TrackingNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Carrier = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    LastEvent = table.Column<string>(type: "TEXT", nullable: true),
                    LastLocation = table.Column<string>(type: "TEXT", nullable: true),
                    LastEventTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstEventTime = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsDelivered = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    FetchedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackingCache", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TurnaroundStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Days = table.Column<double>(type: "REAL", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnaroundStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedJmsPassword = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkshopTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Frequency = table.Column<string>(type: "TEXT", nullable: false),
                    Done = table.Column<bool>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkshopTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomsFda510ks_Number",
                table: "CustomsFda510ks",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomsManufacturers_Name",
                table: "CustomsManufacturers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanEquipmentStatuses_Label",
                table: "LoanEquipmentStatuses",
                column: "Label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReportRuns_Month_CompanyId",
                table: "MonthlyReportRuns",
                columns: new[] { "Month", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyReportRuns_State",
                table: "MonthlyReportRuns",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Repairers_Name",
                table: "Repairers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JmsOutbox_State_Username",
                table: "JmsOutbox",
                columns: new[] { "State", "Username" });

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCustomers_DisplayName",
                table: "ScopeCustomers",
                column: "DisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActionTimeSavings");

            migrationBuilder.DropTable(
                name: "ApexPriceLines");

            migrationBuilder.DropTable(
                name: "CompanyGroupOverrides");

            migrationBuilder.DropTable(
                name: "CustomsFda510ks");

            migrationBuilder.DropTable(
                name: "CustomsManufacturers");

            migrationBuilder.DropTable(
                name: "CustomsModels");

            migrationBuilder.DropTable(
                name: "HighlightedJobs");

            migrationBuilder.DropTable(
                name: "IgnoredJobStatuses");

            migrationBuilder.DropTable(
                name: "InterestedModels");

            migrationBuilder.DropTable(
                name: "JobOfferOverlays");

            migrationBuilder.DropTable(
                name: "LoanEquipmentOverlays");

            migrationBuilder.DropTable(
                name: "LoanEquipmentStatuses");

            migrationBuilder.DropTable(
                name: "LoanModelSubstitutes");

            migrationBuilder.DropTable(
                name: "ManufacturerAbbreviations");

            migrationBuilder.DropTable(
                name: "MonthlyReportRuns");

            migrationBuilder.DropTable(
                name: "MonthlyReportSites");

            migrationBuilder.DropTable(
                name: "QuoteNotices");

            migrationBuilder.DropTable(
                name: "Repairers");

            migrationBuilder.DropTable(
                name: "JmsOutages");

            migrationBuilder.DropTable(
                name: "JmsOutbox");

            migrationBuilder.DropTable(
                name: "ScopeCustomers");

            migrationBuilder.DropTable(
                name: "ScopeExclusions");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TeamNotices");

            migrationBuilder.DropTable(
                name: "TimesheetTaskLog");

            migrationBuilder.DropTable(
                name: "TrackingCache");

            migrationBuilder.DropTable(
                name: "TurnaroundStats");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "WorkshopTasks");
        }
    }
}
