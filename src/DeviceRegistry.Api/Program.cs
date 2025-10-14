using Microsoft.EntityFrameworkCore;
using Innovia.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<InnoviaDbContext>(o => 
    o.UseNpgsql(builder.Configuration.GetConnectionString("Db")));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var app = builder.Build();

// Ensure database and tables exist (quick-start dev convenience)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InnoviaDbContext>();
    db.Database.EnsureCreated();
}

// Enable Swagger always (not only in Development)
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "DeviceRegistry.Api v1");
    c.RoutePrefix = "swagger";
});
// Redirect root to Swagger UI for convenience
app.MapGet("/", () => Results.Redirect("/swagger"));

// Endpoint för skapa tenant.
app.MapPost("/api/tenants", async (InnoviaDbContext db, Tenant t) => {
    db.Tenants.Add(t); await db.SaveChangesAsync(); return Results.Created($"/api/tenants/{t.Id}", t);
});

// Endpoint för att ska en device/sensor för en specifik tenant. 
app.MapPost("/api/tenants/{tenantId:guid}/devices", async (Guid tenantId, InnoviaDbContext db, Device d) => {
    // Sätter så att device tenantId är samma som tenantId. Alltså kopplar device till rätt tenant genom att sätta TenantId. 
    d.TenantId = tenantId;
    // Lägger till och sparar device i databasen. 
    db.Devices.Add(d); await db.SaveChangesAsync();
    // Device returneras 201 created med url till den nya enheten och enheten själv som json. 
    return Results.Created($"/api/tenants/{tenantId}/devices/{d.Id}", d);
});

// Hämtar en specifik device via tenantId och deviceId. Returnerar 404 om inte hittad. 
app.MapGet("/api/tenants/{tenantId:guid}/devices/{deviceId:guid}", async (Guid tenantId, Guid deviceId, InnoviaDbContext db) => {
    var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == deviceId);
    return d is null ? Results.NotFound() : Results.Ok(d);
});

// List all devices for a tenant. 
//Lista på alla enheter för en tenant.
app.MapGet("/api/tenants/{tenantId:guid}/devices",
    async (Guid tenantId, InnoviaDbContext db) =>
{
    var list = await db.Devices
        .Where(d => d.TenantId == tenantId)
        .ToListAsync();
    return Results.Ok(list);
});

// Lookup tenant by slug (for cross-service resolution). 
// Hämta tenant via slug.
app.MapGet("/api/tenants/by-slug/{slug}",
    async (string slug, InnoviaDbContext db) =>
{
    var t = await db.Tenants.FirstOrDefaultAsync(x => x.Slug == slug);
    return t is null ? Results.NotFound() : Results.Ok(t);
});

// Lookup device by serial within a tenant (for cross-service resolution)
// Hämtar en specifik device/enhet via serial nummer. 
app.MapGet("/api/tenants/{tenantId:guid}/devices/by-serial/{serial}",
    async (Guid tenantId, string serial, InnoviaDbContext db) =>
{
    var d = await db.Devices.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Serial == serial);
    return d is null ? Results.NotFound() : Results.Ok(d);
});

app.Run();

// här skapas dbset för innoviaDbContext.
public class InnoviaDbContext : DbContext
{
    public InnoviaDbContext(DbContextOptions<InnoviaDbContext> o) : base(o) { }
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Device> Devices => Set<Device>();
}

// Här skapas klasserna för att man ska kunna skapa tenant och device. 
public class Tenant { public Guid Id {get; set;} = Guid.NewGuid(); public string Name {get; set;} = ""; public string Slug {get; set;} = ""; }
public class Device { public Guid Id {get; set;} = Guid.NewGuid(); public Guid TenantId {get; set;} public Guid? RoomId {get; set;} public string Model {get; set;} = ""; public string Serial {get; set;} = ""; public string Status {get; set;} = "active"; }
