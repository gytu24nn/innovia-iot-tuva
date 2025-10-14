namespace Innovia.Shared.DTOs;

// en en skild mätning från en sensor. 
public record MetricDto(string Type, double Value, string? Unit);

// Denna klass är för paket av mätningar som skickas från en enhet till ingest.gateway. 
// Man kan tänka sig att den används för en leveans med flera mätvärden i taget.
// Används i simulatorn för att skapa och skicka detta objekt via MQTT. 
// Den används också i Ingest.Gateway för att ta emot de, validera det och spara data i databasen. 
public class MeasurementBatch
{
    public string DeviceId { get; set; } = default!;
    public string ApiKey { get; set; } = default!;
    public DateTimeOffset Timestamp { get; set; }
    public List<MetricDto> Metrics { get; set; } = new();
}
