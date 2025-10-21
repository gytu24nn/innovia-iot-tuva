// NOTE to maintainers:
// This file scaffolds a minimal, developer-friendly Rules Engine MVP so teams can
// plug in rule evaluation without first wiring all the plumbing.
// Packages you likely need in the .csproj (add via PackageReference):
//   - Microsoft.EntityFrameworkCore
//   - Npgsql.EntityFrameworkCore.PostgreSQL
//   - Microsoft.AspNetCore.SignalR.Client
//
// Connection strings are read from appsettings.json or environment variables:
//   - ConnectionStrings:RulesDb  (default: Host=localhost;Username=postgres;Password=password;Database=innovia_rules)
//   - ConnectionStrings:IngestDb (default: Host=localhost;Username=postgres;Password=password;Database=innovia_ingest)
// Hub URL:
//   - Realtime:HubUrl (default: http://localhost:5103/hub/telemetry)
//
// TODOs are intentionally terse and technical; treat them as guidance...

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Trim noisy EF Core SQL logs
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.Warning);

// --- Config (with sensible defaults for local dev) ---
var rulesConn = builder.Configuration.GetConnectionString("RulesDb")
    ?? "Host=localhost;Username=postgres;Password=password;Database=innovia_rules";
var ingestConn = builder.Configuration.GetConnectionString("IngestDb")
    ?? "Host=localhost;Username=postgres;Password=password;Database=innovia_ingest";
var hubUrl = builder.Configuration.GetSection("Realtime")["HubUrl"]
    ?? "http://localhost:5103/hub/telemetry";

// --- Services ---
builder.Services.AddDbContext<RulesDbContext>(o => o.UseNpgsql(rulesConn));
builder.Services.AddDbContext<IngestReadDbContext>(o => o.UseNpgsql(ingestConn));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SignalR client to publish alerts in real time
builder.Services.AddSingleton<HubConnection>(_ =>
    new HubConnectionBuilder()
        .WithUrl(hubUrl)
        .WithAutomaticReconnect()
        .Build());

// Background worker
builder.Services.AddHostedService<RulesWorker>();

// Minimal admin/read endpoints (optional but useful for MVP)
var app = builder.Build();

// Enable Swagger always (not only in Development)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "DeviceRegistry.Api v1");
    c.RoutePrefix = string.Empty;
});
// Redirect root to Swagger UI for convenience
// app.MapGet("/", () => Results.Redirect("/swagger"));


// Ensure DB exists (MVP convenience). In production, prefer EF migrations.
using (var scope = app.Services.CreateScope())
{
    var rulesDb = scope.ServiceProvider.GetRequiredService<RulesDbContext>();
    rulesDb.Database.EnsureCreated();
}

// --- Endpoints ---
// Create a simple rule (device-scoped). Example body:
// { "tenantId":"...", "deviceId":"...", "type":"temperature", "op":">", "threshold": 28.0, "cooldownSeconds": 300, "enabled": true }
app.MapPost("/rules", async (RuleCreateDto dto, RulesDbContext db) =>
{
    var row = new RuleRow
    {
        Id = Guid.NewGuid(),
        TenantId = dto.TenantId,
        DeviceId = dto.DeviceId,
        Type = dto.Type,
        Operator = dto.Op,
        Threshold = dto.Threshold,
        CooldownSeconds = dto.CooldownSeconds ?? 300,
        Enabled = dto.Enabled ?? true,
        CreatedAt = DateTimeOffset.UtcNow
    };
    db.Rules.Add(row);
    await db.SaveChangesAsync();
    return Results.Created($"/rules/{row.Id}", row);
});

// List rules
app.MapGet("/rules", async (RulesDbContext db) =>
{
    var rules = await db.Rules
        .OrderByDescending(r => r.CreatedAt)
        .ToListAsync();
    return Results.Ok(rules);
});

// List latest alerts (optional filters)
app.MapGet("/alerts", async (Guid? tenantId, Guid? deviceId, string? type, DateTimeOffset? from, DateTimeOffset? to, RulesDbContext db) =>
{
    var q = db.Alerts.AsQueryable();
    if (tenantId is Guid t) q = q.Where(a => a.TenantId == t);
    if (deviceId is Guid d) q = q.Where(a => a.DeviceId == d);
    if (!string.IsNullOrWhiteSpace(type)) q = q.Where(a => a.Type == type);
    if (from is DateTimeOffset f) q = q.Where(a => a.Time >= f);
    if (to is DateTimeOffset tt) q = q.Where(a => a.Time <= tt);

    var list = await q.OrderByDescending(a => a.Time).Take(200).ToListAsync();
    return Results.Ok(list);
});

app.MapGet("/", () => Results.Ok(new { service = "Rules.Engine", status = "ok" }));

// Start SignalR connection on boot
using (var scope = app.Services.CreateScope())
{
    var conn = scope.ServiceProvider.GetRequiredService<HubConnection>();
    try
    {
        await conn.StartAsync();
        app.Logger.LogInformation("Connected to Realtime Hub at {HubUrl}", hubUrl);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to connect to Realtime Hub at {HubUrl}. Alerts will be stored but not pushed until reconnected.", hubUrl);
    }
}

app.Run();


// ===========================
// Background worker
// ===========================
public class RulesWorker : BackgroundService
{
    private readonly ILogger<RulesWorker> _log;
    private readonly RulesDbContext _rules;
    private readonly IngestReadDbContext _ingest;
    private readonly HubConnection _hub;

    public RulesWorker(ILogger<RulesWorker> log, RulesDbContext rules, IngestReadDbContext ingest, HubConnection hub)
    {
        _log = log;
        _rules = rules;
        _ingest = ingest;
        _hub = hub;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Rules engine started (poll=10s, mode=instant)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // TODO: If throughput grows, prefer processing only newly-seen measurements
                // (e.g., watermark per device/type). MVP: evaluate "latest point" for each rule.

                var activeRules = await _rules.Rules
                    .Where(r => r.Enabled)
                    .ToListAsync(stoppingToken);

                foreach (var r in activeRules)
                {
                    // Fetch the latest measurement for the rule scope (device + type).
                    var latest = await _ingest.Measurements
                        .Where(m => m.TenantId == r.TenantId
                                    && (r.DeviceId == null || m.DeviceId == r.DeviceId)
                                    && m.Type == r.Type)
                        .OrderByDescending(m => m.Time)
                        .Select(m => new { m.Value, m.Time, m.DeviceId })
                        .FirstOrDefaultAsync(stoppingToken);

                    if (latest is null) continue;

                    if (Matches(r.Operator, latest.Value, r.Threshold))
                    {
                        // Cooldown: avoid spamming duplicate alerts for the same rule/device/type
                        var cooldownAgo = DateTimeOffset.UtcNow.AddSeconds(-(r.CooldownSeconds ?? 300));
                        var existsRecent = await _rules.Alerts
                            .AnyAsync(a => a.RuleId == r.Id && a.DeviceId == latest.DeviceId && a.Time >= cooldownAgo, stoppingToken);

                        if (existsRecent) continue;

                        var alert = new AlertRow
                        {
                            Id = Guid.NewGuid(),
                            RuleId = r.Id,
                            TenantId = r.TenantId,
                            DeviceId = latest.DeviceId,
                            Type = r.Type,
                            Value = latest.Value,
                            Time = DateTimeOffset.UtcNow,
                            Severity = "warning",
                            Message = $"Rule {r.Operator} {r.Threshold} hit for {r.Type} (value={latest.Value})"
                        };

                        _rules.Alerts.Add(alert);
                        await _rules.SaveChangesAsync(stoppingToken);

                        // Push to realtime (requires Realtime.Hub to expose a matching server method)
                        // TODO: Implement PublishAlert on the hub; client method name can be "alertRaised" for browser subscribers.
                        try
                        {
                            await _hub.InvokeAsync("PublishAlert", new
                            {
                                TenantId = alert.TenantId,
                                DeviceId = alert.DeviceId,
                                Type = alert.Type,
                                Value = alert.Value,
                                Time = alert.Time,
                                RuleId = alert.RuleId,
                                Severity = alert.Severity,
                                Message = alert.Message
                            }, cancellationToken: stoppingToken);
                        }
                        catch (Exception pushEx)
                        {
                            _log.LogWarning(pushEx, "Failed to push alert to realtime hub (will remain stored).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled error during rule evaluation cycle.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private static bool Matches(string op, double value, double threshold) => op switch
    {
        ">"  => value >  threshold,
        ">=" => value >= threshold,
        "<"  => value <  threshold,
        "<=" => value <= threshold,
        "==" => Math.Abs(value - threshold) < 1e-9,
        "!=" => Math.Abs(value - threshold) >= 1e-9,
        _    => false // TODO: extend as needed
    };
}


// ===========================
// EF Core data model
// ===========================
public class RulesDbContext : DbContext
{
    public RulesDbContext(DbContextOptions<RulesDbContext> options) : base(options) { }
    public DbSet<RuleRow> Rules => Set<RuleRow>();
    public DbSet<AlertRow> Alerts => Set<AlertRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RuleRow>().ToTable("Rules").HasKey(r => r.Id);
        modelBuilder.Entity<AlertRow>().ToTable("Alerts").HasKey(a => a.Id);

        modelBuilder.Entity<RuleRow>()
            .HasIndex(r => new { r.TenantId, r.DeviceId, r.Type, r.Enabled });

        modelBuilder.Entity<AlertRow>()
            .HasIndex(a => new { a.TenantId, a.DeviceId, a.Type, a.Time });

        base.OnModelCreating(modelBuilder);
    }
}

public class IngestReadDbContext : DbContext
{
    public IngestReadDbContext(DbContextOptions<IngestReadDbContext> options) : base(options) { }
    public DbSet<MeasurementRow> Measurements => Set<MeasurementRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MeasurementRow>().ToTable("Measurements").HasKey(m => m.Id);
        base.OnModelCreating(modelBuilder);
    }
}

// --- Tables ---
public class RuleRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? DeviceId { get; set; } // null => applies to all devices in tenant (stretch)
    public string Type { get; set; } = ""; // e.g., "temperature"
    public string Operator { get; set; } = ">"; // ">", "<", ">=", "<=", "==", "!="
    public double Threshold { get; set; }
    public int? CooldownSeconds { get; set; } = 300;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AlertRow
{
    public Guid Id { get; set; }
    public Guid RuleId { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public double Value { get; set; }
    public DateTimeOffset Time { get; set; }
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
}

// Mirror of Ingest measurement (read-only model)
public class MeasurementRow
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public double Value { get; set; }
}

// --- DTOs ---
public record RuleCreateDto(
    Guid TenantId,
    Guid? DeviceId,
    string Type,
    string Op,
    double Threshold,
    int? CooldownSeconds,
    bool? Enabled
);
