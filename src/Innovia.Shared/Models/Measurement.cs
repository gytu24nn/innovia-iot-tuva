namespace Innovia.Shared.Models;

// en mer intern modell av en mätning väldigt lik den som används i Ingest.Gateway.  
public class Measurement
{
    public DateTimeOffset Time { get; set; }
    public Guid TenantId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid SensorId { get; set; }
    public int TypeId { get; set; }
    public double Value { get; set; }
}
