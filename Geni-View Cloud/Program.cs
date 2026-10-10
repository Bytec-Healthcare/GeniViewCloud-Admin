// Program.cs — ASP.NET Core 8 entry point
// Replaces: Global.asax.cs, Startup.cs (OWIN), Startup.Auth.cs, HangfireBootstrapper.cs, WebHost.cs
//
// Phase 3: Full middleware pipeline wired up.
// Phase 4: Identity migrations + seed will be triggered from the EnsureDatabaseAsync call below.
// Phase 6: MQTTBackgroundService registered here.
// Phase 8: WebOptimizer bundles registered here.

using GeniView.Cloud.Common;
using GeniView.Cloud.Hubs;
using GeniView.Cloud.Models;
using GeniView.Cloud.Repository;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Web;
using Npgsql;
using System;
using System.Threading.Tasks;
using WebOptimizer;

// Npgsql 8 maps DateTime to 'timestamp with time zone' and rejects DateTimeKind.Local.
// Enable legacy behaviour so existing code that uses DateTime.Now continues to work.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// ── Bootstrap NLog early so startup errors are captured ────────────────────
var logger = LogManager.Setup()
                       .LoadConfigurationFromAppSettings()
                       .GetCurrentClassLogger();
logger.Info("Application starting.");

try
{
    var builder = WebApplication.CreateBuilder(args);
    var configuration = builder.Configuration;

    // ── NLog as the logging provider ────────────────────────────────────────
    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    // ── MVC + Razor views ───────────────────────────────────────────────────
    builder.Services.AddControllersWithViews()
        .AddJsonOptions(opts =>
        {
            // Preserve PascalCase JSON serialization to match the old ASP.NET MVC
            // Newtonsoft.Json default. All existing JavaScript reads property names
            // in PascalCase (e.g. data.PowerModulesCount, data.HighSoCCount).
            opts.JsonSerializerOptions.PropertyNamingPolicy = null;
        })
        .AddRazorOptions(opts =>
        {
            // Preserve the legacy partial view location used by NewPartialViewEngine.
            opts.ViewLocationFormats.Add("/Views/PartialViews/{0}.cshtml");
            opts.AreaViewLocationFormats.Add("/Areas/{2}/Views/PartialViews/{0}.cshtml");
        });

    // ── EF Core — Identity DB ───────────────────────────────────────────────
    builder.Services.AddDbContext<ApplicationDbContext>(opts =>
        opts.UseNpgsql(
            configuration.GetConnectionString("GeniViewCloudIdentityRepository"),
            npgsql => npgsql.EnableRetryOnFailure()));

    // ── EF Core — Application data DB ──────────────────────────────────────
    builder.Services.AddDbContext<GeniViewCloudDataRepository>(opts =>
        opts.UseNpgsql(
            configuration.GetConnectionString("GeniViewCloudDataRepository"),
            npgsql => npgsql.EnableRetryOnFailure()));

    // ── ASP.NET Core Identity ───────────────────────────────────────────────
    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(opts =>
    {
        // Password policy (matches original IdentityConfig.cs)
        opts.Password.RequireDigit           = false;
        opts.Password.RequireLowercase       = false;
        opts.Password.RequireNonAlphanumeric = false;
        opts.Password.RequireUppercase       = false;
        opts.Password.RequiredLength         = 6;

        // Lockout policy
        opts.Lockout.DefaultLockoutTimeSpan  = TimeSpan.FromMinutes(
            configuration.GetValue<int>("AppSettings:UserLockoutTimeInMinutes", 5));
        opts.Lockout.MaxFailedAccessAttempts = 5;
        opts.Lockout.AllowedForNewUsers      = true;

        // User settings
        opts.User.RequireUniqueEmail = true;

        // Sign-in: email confirmation not required during initial migration
        opts.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

    // Cookie config — matches original FormsAuthentication behaviour
    builder.Services.ConfigureApplicationCookie(opts =>
    {
        opts.LoginPath        = "/Account/Login";
        opts.LogoutPath       = "/Account/LogOff";
        opts.AccessDeniedPath = "/Account/Login";
        opts.ExpireTimeSpan   = TimeSpan.FromDays(14);
        opts.SlidingExpiration = true;
    });

    // ── Session ─────────────────────────────────────────────────────────────
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(opts =>
    {
        opts.IdleTimeout        = TimeSpan.FromMinutes(30);
        opts.Cookie.HttpOnly    = true;
        opts.Cookie.IsEssential = true;
    });

    // ── IHttpContextAccessor (used by SessionHelper) ────────────────────────
    builder.Services.AddHttpContextAccessor();

    // ── In-memory cache (used by MemCacheHelper) ────────────────────────────
    builder.Services.AddMemoryCache();

    // ── DataProtection — persist keys so sessions/cookies survive restarts ───
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new System.IO.DirectoryInfo(
            System.IO.Path.Combine(builder.Environment.ContentRootPath, "DataProtection-Keys")));

    // ── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ── WebOptimizer (Phase 8) — CSS + JS bundles ────────────────────────────
    builder.Services.AddWebOptimizer(pipeline =>
    {
        // Main layout CSS bundle
        pipeline.AddCssBundle("/bundles/main.css",
            "css/main.css",
            "css/global-nav.css",
            "css/dataTables.min.css",
            "css/alertify.min.css");

        // Admin layout CSS bundle
        pipeline.AddCssBundle("/bundles/admin.css",
            "css/bootstrap-colorpicker.min.css",
            "css/alertify.min.css",
            "css/dataTables.min.css");

        // Core JS bundle (jQuery + Bootstrap + common utilities)
        pipeline.AddJavaScriptBundle("/bundles/lib.js",
            "js/jquery-3.7.1.min.js",
            "js/bootstrap.min.js",
            "js/jquery.matchHeight.js",
            "js/alertify.min.js");

        // App JS bundle (shared across all pages)
        pipeline.AddJavaScriptBundle("/bundles/app.js",
            "js/Custom Scripts/global-filters.js",
            "js/Custom Scripts/datatables.min.js",
            "js/Custom Scripts/notification.js",
            "js/Custom Scripts/app.js");

        // Admin JS bundle
        pipeline.AddJavaScriptBundle("/bundles/admin-app.js",
            "js/Custom Scripts/datatables.min.js",
            "js/jquery.matchHeight.js",
            "js/alertify.min.js",
            "js/Custom Scripts/bootstrap-colorpicker.min.js",
            "js/Custom Scripts/notification.js",
            "js/Custom Scripts/app.js");
    });

    // ── Pre-flight: create all 3 PostgreSQL databases if they don't exist ──────
    // Hangfire.PostgreSql opens a live connection during DI construction (builder.Build),
    // so the target database must already exist before AddHangfire is called.
    try { EnsurePostgresqlDatabasesExist(configuration); }
    catch (Exception ex) { logger.Error(ex, "Pre-flight database creation failed — check PostgreSQL credentials in appsettings.json."); }

    // ── Hangfire — use PostgreSQL storage ───────────────────────────────────
    builder.Services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UsePostgreSqlStorage(
            opts => opts.UseNpgsqlConnection(
                configuration.GetConnectionString("GeniViewCloudHangfireRepository")),
            new PostgreSqlStorageOptions
            {
                QueuePollInterval            = TimeSpan.FromSeconds(15),
                PrepareSchemaIfNecessary     = true,
                UseNativeDatabaseTransactions = true
            }));
    builder.Services.AddHangfireServer();

    // ── Data repositories (scoped per request) ───────────────────────────────
    builder.Services.AddScoped<GeniView.Cloud.Repository.IdentityDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.DevicesDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.BatteriesDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.DashboardDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.DashboardPopupRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.CommunitiesDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.GroupsDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.AgentsDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.ApplicationLogsDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.ApplicationUpdatesDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.DeviceEventsDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.DeviceEventRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Repository.G3BatteryDataRepository>();
    builder.Services.AddScoped<GeniView.Cloud.Services.CentralSyncProcessor>();

    // ── WCF → REST service classes (Phase 9) ────────────────────────────────
    builder.Services.AddScoped<GeniView.Cloud.Services.IApplicationUpdateService,
                               GeniView.Cloud.Services.GeniViewApplicationUpdateService>();
    builder.Services.AddScoped<GeniView.Cloud.Services.IDeviceDataPublishService,
                               GeniView.Cloud.Services.GeniViewDeviceDataPublishService>();

    // ── MQTT singleton + BackgroundService (Phase 6) ────────────────────────
    builder.Services.AddSingleton<MQTTHelper>(sp =>
        new MQTTHelper(sp.GetRequiredService<IConfiguration>()));
    builder.Services.AddHostedService<MQTTBackgroundService>();

    // ─────────────────────────────────────────────────────────────────────────
    var app = builder.Build();
    // ─────────────────────────────────────────────────────────────────────────

    // ── Route MQTTHelper.Instance to the DI singleton (the one that connects) ─
    MQTTHelper.SetInstance(app.Services.GetRequiredService<MQTTHelper>());

    // ── Initialise static helpers that need IConfiguration ──────────────────
    GlobalSettings.Initialize(configuration);

    // ── Initialise SessionHelper with IHttpContextAccessor ──────────────────
    var httpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();
    SessionHelper.Configure(httpContextAccessor);

    // ── Set Global._serverPath for MailHelper / OTA file paths ──────────────
    Global._serverPath = app.Environment.ContentRootPath;

    // ── Initialise MemCacheHelper with the DI-provided IMemoryCache ──────────
    Global._memCacheHelper = new RenityArtemis.Web.Common.MemCacheHelper(
        app.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());

    // ── Apply EF Core migrations and seed the database ──────────────────────
    await EnsureDatabaseAsync(app);

    // ── Exception handling ───────────────────────────────────────────────────
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }
    else
    {
        app.UseDeveloperExceptionPage();
    }

    // ── Middleware pipeline (ORDER MATTERS) ──────────────────────────────────
    app.UseHttpsRedirection();
    app.UseWebOptimizer();      // must be before UseStaticFiles
    app.UseStaticFiles();
    // Serve OTA and other uploaded files stored outside wwwroot.
    var otaFilesPath = System.IO.Path.Combine(app.Environment.ContentRootPath, "Files");
    System.IO.Directory.CreateDirectory(otaFilesPath);
    app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(otaFilesPath),
        RequestPath = "/Files"
    });
    app.UseRouting();
    app.UseSession();           // must be before Authentication
    app.UseAuthentication();
    app.UseAuthorization();

    // ── Hangfire dashboard (/hangfire) — restricted to Application Admin ─────
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[] { new HangFireAuthorizationFilter() }
    });

    // ── SignalR hub ──────────────────────────────────────────────────────────
    app.MapHub<NotificationHub>("/notificationHub");

    // ── MVC routes ───────────────────────────────────────────────────────────
    // Admin area route must come before the default route
    app.MapControllerRoute(
        name: "areas",
        pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Dashboard}/{action=Index}/{id?}");

    // ── Hangfire recurring jobs (Phase 6) ────────────────────────────────────
    // Hangfire's built-in AspNetCoreJobActivator resolves LogApiController
    // from a DI scope per job execution — no direct instantiation needed.
    new HFScheduler().Setting();

    logger.Info("Application started successfully.");
    app.Run();
}
catch (Exception ex)
{
    logger.Fatal(ex, "Application terminated unexpectedly.");
    throw;
}
finally
{
    LogManager.Shutdown();
}

// ── Helper: create all 3 PostgreSQL databases + seed on first run ────────────
// EnsureCreatedAsync creates the full schema from the EF Core model if the
// Creates all three databases and their schemas on first run.
// If a database exists but is empty (left over from a failed previous startup),
// it is dropped so EnsureCreatedAsync can create it fresh with all tables.
static async Task EnsureDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var services = scope.ServiceProvider;
    var log = services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebApplication>>();
    var config = services.GetRequiredService<IConfiguration>();

    try
    {
        // Drop each app database if it exists but has zero user tables.
        // EnsureCreatedAsync only creates tables when it also creates the database —
        // if the database already exists (even empty) it does nothing, so we drop it
        // first and let EnsureCreatedAsync do a clean database + table creation.
        DropIfEmpty(config.GetConnectionString("GeniViewCloudIdentityRepository"), log);
        DropIfEmpty(config.GetConnectionString("GeniViewCloudDataRepository"), log);

        // Identity DB — create database + all ASP.NET Core Identity tables
        var identityDb = services.GetRequiredService<ApplicationDbContext>();
        await identityDb.Database.EnsureCreatedAsync();

        // Application data DB — create database + all business tables + analytics views
        var dataDb = services.GetRequiredService<GeniViewCloudDataRepository>();
        await dataDb.Database.EnsureCreatedAsync();
        GeniViewCloudDataRepositoryInitializer.Seed(dataDb);

        // Hangfire DB — Hangfire.PostgreSql creates its own schema automatically
        // via PrepareSchemaIfNecessary = true (configured in AddHangfire above).

        await SeedAsync(services, log);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Database initialisation failed. The app will still start.");
    }
}

// Drops the named PostgreSQL database if it exists but contains no user tables.
// Safe to call on every startup: no-op if DB doesn't exist or already has tables.
static void DropIfEmpty(string connectionString, Microsoft.Extensions.Logging.ILogger log)
{
    var target = new NpgsqlConnectionStringBuilder(connectionString);
    var dbName = target.Database;
    try
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var countCmd = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
              AND table_name != '__EFMigrationsHistory'
            """, conn);
        var tableCount = Convert.ToInt64(countCmd.ExecuteScalar());
        conn.Close();

        if (tableCount > 0) return; // DB has real app tables — leave it alone

        log.LogInformation("Database {Db} has no app tables (may only have __EFMigrationsHistory) — dropping so EnsureCreatedAsync can create it fresh.", dbName);

        target.Database = "postgres";
        using var pgConn = new NpgsqlConnection(target.ConnectionString);
        pgConn.Open();

        // Kick any lingering connections so DROP DATABASE can proceed
        using var termCmd = new NpgsqlCommand(
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{dbName}' AND pid<>pg_backend_pid()",
            pgConn);
        termCmd.ExecuteNonQuery();

        using var dropCmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{dbName}\"", pgConn);
        dropCmd.ExecuteNonQuery();
    }
    catch
    {
        // DB does not exist or is unreachable — EnsureCreatedAsync will handle creation
    }
}



// Pre-creates only the Hangfire database before the DI container is built.
// Hangfire.PostgreSql opens a real connection during builder.Build(), so
// GeniViewCloudHangfire must exist at that point.
// Identity and Data databases are created later by EnsureCreatedAsync (which
// also creates all tables). Only the Hangfire DB is handled here because
// EnsureCreatedAsync creates both the database AND its tables in one go —
// pre-creating those two databases would leave them empty and cause
// EnsureCreatedAsync to skip table creation.
static void EnsurePostgresqlDatabasesExist(IConfiguration configuration)
{
    var target = new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("GeniViewCloudHangfireRepository"));
    var dbName = target.Database;

    target.Database = "postgres"; // connect to maintenance DB on the same server
    using var conn = new NpgsqlConnection(target.ConnectionString);
    conn.Open();

    using var check = new NpgsqlCommand(
        "SELECT 1 FROM pg_database WHERE datname = @db", conn);
    check.Parameters.AddWithValue("db", dbName);
    var exists = check.ExecuteScalar() != null;

    if (!exists)
    {
        using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
        create.ExecuteNonQuery();
    }
}

static async Task SeedAsync(IServiceProvider services, Microsoft.Extensions.Logging.ILogger log)
{
    var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

    // Seed roles
    string[] roles =
    {
        "Application Admin",
        "Application User",
        "Community Admin",
        "Community Group Admin",
        "Community User"
    };

    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
            log.LogInformation("Created role: {Role}", role);
        }
    }

    // Seed default Application Admin user
    const string adminEmail    = "admin@bytec.com";
    const string adminPassword = "Admin@123!";

    if (await userManager.FindByEmailAsync(adminEmail) == null)
    {
        var admin = new ApplicationUser
        {
            UserName              = adminEmail,
            Email                 = adminEmail,
            FullName              = "Application Admin",
            EmailConfirmed        = true,
            IsNotificationEnable  = true,
            LockoutEnabled        = false
        };

        var result = await userManager.CreateAsync(admin, adminPassword);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(admin, "Application Admin");
            log.LogInformation("Seeded default admin user: {Email}", adminEmail);
        }
        else
        {
            foreach (var e in result.Errors)
                log.LogWarning("Seed admin error: {Code} — {Description}", e.Code, e.Description);
        }
    }
}
