using Microsoft.Extensions.Logging;
using Innovia.Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
// Trim noisy logs: hide EF Core SQL and HttpClient chatter
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddDbContext<IngestDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddScoped<IngestService>();
builder.Services.AddScoped<IValidator<MeasurementBatch>, MeasurementBatchValidator>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
// Registry HTTP client + config
builder.Services.AddHttpClient<DeviceRegistryClient>();
builder.Services.AddSingleton<DeviceRegistryConfig>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("DeviceRegistry");
    return new DeviceRegistryConfig { BaseUrl = cfg?["BaseUrl"] ?? "http://localhost:5101" };
});
// Realtime publisher (SignalR client)
// Här skapas en koppling till SignalR huber för kunna skicka data i realtid till ex frontend
// Kopplingen startas inte här. 
builder.Services.AddSingleton(new RealtimeConfig
{
    HubUrl = "http://localhost:5103/hub/telemetry"
});
builder.Services.AddSingleton<HubConnection>(sp =>
{
    var cfg = sp.GetRequiredService<RealtimeConfig>();
    return new HubConnectionBuilder()
        .WithUrl(cfg.HubUrl)
        .WithAutomaticReconnect()
        .Build();
});
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Här skapas appen. 
var app = builder.Build();

app.UseCors();

// Start SignalR hub connection. Här startar SignalR-anslutningen som skapades innan.
using (var scope = app.Services.CreateScope())
{
    var hub = scope.ServiceProvider.GetRequiredService<HubConnection>();
    await hub.StartAsync();
}

// Ensure database and tables exist (quick-start dev convenience)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IngestDbContext>();
    db.Database.EnsureCreated();
}

// Enable Swagger always (not only in Development)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Ingest.Gateway v1");
    c.RoutePrefix = "swagger";
});
// Redirect root to Swagger UI for convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

// Här är en post om datan skickas via HTTP post. 
app.MapPost("/ingest/http/{tenant}", async (string tenant, MeasurementBatch payload, IValidator<MeasurementBatch> validator, IngestService ingest, ILogger<Program> log) =>
{
    // Här valideras de om de har rätt innehåll. 
    var result = await validator.ValidateAsync(payload);
    if (!result.IsValid)
    {
        log.LogWarning("Validation failed for ingest payload (tenant: {Tenant}, serial: {Serial}): {Errors}", tenant, payload?.DeviceId, result.Errors);
        return Results.BadRequest(result.Errors);
    }

    // Om de som skickas har rätt innehåll skickas de vidare till IngestService där datan sparas i databasen. 
    await ingest.ProcessAsync(tenant, payload);
    log.LogInformation("Ingested {Count} metrics for serial {Serial} in tenant {Tenant} at {Time}", payload.Metrics.Count, payload.DeviceId, tenant, payload.Timestamp);
    return Results.Accepted();
});

// Detta är en get för att man ska kunna kontrollera om datan sparades i databasen. 
app.MapGet("/ingest/debug/device/{deviceId:guid}", async (Guid deviceId, IngestDbContext db) =>
{
    var count = await db.Measurements.Where(m => m.DeviceId == deviceId).CountAsync();
    var latest = await db.Measurements.Where(m => m.DeviceId == deviceId).OrderByDescending(m => m.Time).Take(5).ToListAsync();
    return Results.Ok(new { deviceId, count, latest });
});

// --- MQTT subscriber: consume Edge.Simulator messages and reuse the same processing pipeline ---
// Här lyssnar MQTT på meddelande från edge.simulator. 
var mqttFactory = new MqttFactory();
var mqttClient = mqttFactory.CreateMqttClient();
var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer("localhost", 1883)
    .Build();

// Subscribe to tenants/{tenantSlug}/devices/{serial}/measurements
// topic som simulatorn använder
var mqttTopic = "tenants/+/devices/+/measurements";

mqttClient.ApplicationMessageReceivedAsync += async e =>
{
    try
    {
        // Om då edge.simulator skickar ett meddelande i MQTT så kommer man in i denna try. 

        // Här delar topicen upp för att hitta tenantSlug och deviceSerial. 
        var topic = e.ApplicationMessage.Topic ?? string.Empty;
        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Expected: tenants/{tenantSlug}/devices/{serial}/measurements
        if (parts.Length >= 5 && parts[0] == "tenants" && parts[2] == "devices")
        {
            var tenantSlug = parts[1];
            var serial = parts[3];

            // Deserialize payload into MeasurementBatch (same shape as HTTP ingest)
            // Här läser man ut JSON från MQTT meddelandet. 
            var payloadBytes = e.ApplicationMessage.PayloadSegment.ToArray();
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var batch = JsonSerializer.Deserialize<MeasurementBatch>(payloadBytes, jsonOptions);

            // Fel hantering om man ej kunde läsa ut ett meddelande ur den. 
            if (batch is null)
            {
                Console.WriteLine($"[MQTT] Skipping: could not deserialize payload on topic '{topic}'");
                return;
            }

            // Ensure batch.DeviceId (serial) is set/consistent
            // Om deviceId saknas i payloaden → sätt den från topicen.
            if (string.IsNullOrWhiteSpace(batch.DeviceId))
            {
                batch.DeviceId = serial;
            }

            // Här skapas en ny tjänst-scope och kör ingestService för att spara de i databasen. 
            using var scope = app.Services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IngestService>();
            await svc.ProcessAsync(tenantSlug, batch);

            Console.WriteLine($"[MQTT] Ingested {batch.Metrics?.Count ?? 0} metrics for serial '{serial}' in tenant '{tenantSlug}' at {batch.Timestamp:o}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[MQTT] Handler error: {ex.Message}");
    }
};


// här görs det ett försök att ansluta till MQTT och prenumerara. 
try
{
    await mqttClient.ConnectAsync(mqttOptions);
    await mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
        .WithTopic(mqttTopic)
        .WithAtLeastOnceQoS()
        .Build());
    Console.WriteLine($"[MQTT] Subscribed to '{mqttTopic}' on localhost:1883");
}
catch (Exception ex)
{
    Console.WriteLine($"[MQTT] Failed to connect/subscribe: {ex.Message}");
}
// --- end MQTT subscriber ---

app.Run();

// Efter här skapas logiken som filen kör på så det skapas inte efter nu utan de används redan. (hade kunnat placera detta i olika filer och importerat in istället)

// en klass som pratar med databasen för att den arver ifrån dbcontext. 
public class IngestDbContext : DbContext
{
    public IngestDbContext(DbContextOptions<IngestDbContext> o) : base(o) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MeasurementRow>().ToTable("Measurements");
        base.OnModelCreating(modelBuilder);
    }

    public DbSet<MeasurementRow> Measurements => Set<MeasurementRow>();
}

// En vanlig klass som används för att strukturera upp hur de ska sparas i databasen.
public class MeasurementRow
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public string Type { get; set; } = "";
    public double Value { get; set; }
}

// Används för att säkerställa att inkommande data är giltig. Så om något av fälten här nedan är tomma returneras 400. 
public class MeasurementBatchValidator : AbstractValidator<MeasurementBatch>
{
    public MeasurementBatchValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.ApiKey).NotEmpty();
        RuleFor(x => x.Metrics).NotEmpty();
    }
}

// den här klassen hanterar alltså att ta emot data och spara den. Den sparar alltså mätningarna i databasen och skickar de vidare till SignalR i realtid. 
public class IngestService
{
    private readonly IngestDbContext _db;
    private readonly DeviceRegistryClient _registry;
    private readonly IRealtimePublisher _rt;
    public IngestService(IngestDbContext db, DeviceRegistryClient registry, IRealtimePublisher rt) { _db = db; _registry = registry; _rt = rt; }
    public async Task ProcessAsync(string tenant, MeasurementBatch payload)
    {
        // Resolve tenant and device using DeviceRegistry (tenant slug + device serial)
        var ids = await _registry.ResolveAsync(tenant, payload.DeviceId);

        foreach (var m in payload.Metrics)
        {
            _db.Measurements.Add(new MeasurementRow
            {
                Time = payload.Timestamp,
                TenantId = ids.TenantId,
                DeviceId = ids.DeviceId,
                Type = m.Type,
                Value = m.Value
            });
        }
        await _db.SaveChangesAsync();

        // Publish in realtime
        foreach (var m in payload.Metrics)
        {
            await _rt.PublishAsync(tenant, ids.DeviceId, m.Type, m.Value, m.Unit, payload.Timestamp);
        }
    }
}

public record DeviceRegistryConfig
{
    public string BaseUrl { get; set; } = "http://localhost:5101";
}

public class DeviceRegistryClient
{
    private readonly HttpClient _http;
    private readonly DeviceRegistryConfig _cfg;
    private readonly Dictionary<string, (Guid TenantId, Guid DeviceId)> _cache = new();

    public DeviceRegistryClient(HttpClient http, DeviceRegistryConfig cfg)
    {
        _http = http;
        _cfg = cfg;
    }


    // Denna del hämtar tenantId och deviceId ifrån DeviceRegistry. 
    public async Task<(Guid TenantId, Guid DeviceId)> ResolveAsync(string tenantSlug, string deviceSerial)
    {
        var cacheKey = $"{tenantSlug}:{deviceSerial}";
        if (_cache.TryGetValue(cacheKey, out var hit)) return hit;

        var tenant = await _http.GetFromJsonAsync<TenantDto>($"{_cfg.BaseUrl}/api/tenants/by-slug/{tenantSlug}")
                    ?? throw new InvalidOperationException($"Tenant slug '{tenantSlug}' not found");

        var device = await _http.GetFromJsonAsync<DeviceDto>($"{_cfg.BaseUrl}/api/tenants/{tenant.Id}/devices/by-serial/{deviceSerial}")
                    ?? throw new InvalidOperationException($"Device serial '{deviceSerial}' not found in tenant '{tenantSlug}'");

        var ids = (tenant.Id, device.Id);
        _cache[cacheKey] = ids;
        return ids;
    }

    private record TenantDto(Guid Id, string Name, string Slug);
    private record DeviceDto(Guid Id, Guid TenantId, Guid? RoomId, string Model, string Serial, string Status);
}

public class RealtimeConfig
{
    public string HubUrl { get; set; } = "http://localhost:5103/hub/telemetry";
}

public interface IRealtimePublisher
{
    Task PublishAsync(string tenantSlug, Guid deviceId, string type, double value, string unit, DateTimeOffset time);
}

// Denna skickad datan vidare till SignalR. 
public class SignalRRealtimePublisher : IRealtimePublisher
{
    private readonly HubConnection _conn;
    public SignalRRealtimePublisher(HubConnection conn) => _conn = conn;

    public async Task PublishAsync(string tenantSlug, Guid deviceId, string type, double value, string unit, DateTimeOffset time)
    {
        var payload = new
        {
            TenantSlug = tenantSlug,
            DeviceId = deviceId,
            Type = type,
            Value = value,
            Unit = unit,
            Time = time
        };
        await _conn.InvokeAsync("PublishMeasurement", payload);
    }
}
