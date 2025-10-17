using Microsoft.AspNetCore.SignalR;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(
                "http://127.0.0.1:5500",
                "http://localhost:5500",
                "http://localhost:5173"    
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();
// här bestämmer den vad för cors den ska använda. 
app.UseCors("Frontend");
app.MapHub<TelemetryHub>("/hub/telemetry").RequireCors("Frontend");
app.Run();

// Här skapas huben och i den får den till sig data och när den får de uppdateras den i realtid.
// alla klienter ansluter som använder den. 
public class TelemetryHub : Hub
{
    // lägger till ansluta klienten till en grupp baserat på tenant.
    // Alla i samma tenant-grupp får samma data. 
    public Task JoinTenant(string tenant) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"tenant:{tenant}");

    // Här tar den emot mätdata ifrån ingest.gateway och skickar till klienterna i samma tenant grupp.  
    public async Task PublishMeasurement(RealtimeMeasurement m)
    {
        await Clients.Group($"tenant:{m.TenantSlug}")
            .SendAsync("measurementReceived", m);
    }
}

// Här definieras strukturen på mätningen som skickats via huben. 
public record RealtimeMeasurement(
    string TenantSlug,
    System.Guid DeviceId,
    string Type,
    double Value,
    string Unit,
    System.DateTimeOffset Time
);
