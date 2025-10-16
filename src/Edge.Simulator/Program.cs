using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using System.Text;
using System.Net.Http.Json;

// Hämta konfiguration från miljövariabler
var tenantSlug = Environment.GetEnvironmentVariable("TENANT_SLUG") ?? "innovia";
var deviceRegistryBase = Environment.GetEnvironmentVariable("DEVICE_REGISTRY_BASE") ?? "http://localhost:5101";
var mqttHost = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
var mqttPort = int.TryParse(Environment.GetEnvironmentVariable("MQTT_PORT"), out var p) ? p : 1883;

System.Console.WriteLine($"Edge.simulator starting... tenantSlug={tenantSlug}, DeviceRegistry={deviceRegistryBase}, MQTT={mqttHost}:{mqttPort}");

// Skapa HTTP-klient och MQTT klient.
var http = new HttpClient { BaseAddress = new Uri(deviceRegistryBase) };

// Här skapas en klient som kan koppla upp sig mot MQTT-brokern. 
var factory = new MqttFactory();
var mqtt = factory.CreateMqttClient();

// Här ställs anslutningen in så att den kopplar till rät localhost.
var mqttOptions = new MqttClientOptionsBuilder()
    .WithTcpServer(mqttHost, mqttPort)
    .Build();

// Här kontrollerar man så att den är ansluten.
try
{
    await mqtt.ConnectAsync(mqttOptions);
    Console.WriteLine("✅ Connected to MQTT broker.");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to connect to MQTT broker: {ex.Message}");
    throw;
}

// Hämta tenant från DeviceRegistry via slug 
var tenant = await http.GetFromJsonAsync<Tenant>($"/api/tenants/by-slug/{tenantSlug}")
    ?? throw new Exception($"Tenant with slug '{tenantSlug}' not found");

// Här avgörs det vad för sensorer/mätvärden per device ska få beroende på vad de inne håller i model. 
string InferMetricType(Device d)
{
    var m = d.Model?.ToLowerInvariant() ?? "";
    if (m.Contains("co2")) return "co2";
    if (m.Contains("temperature")) return "temperature";
    if (m.Contains("humidity")) return "humidity";
    if (m.Contains("light")) return "light";
    if (m.Contains("motion")) return "motion";

    return "";
};

// Efter det skapas random generator och variabler för loop.

// Här skapas en random för att kunna få ut random data till låsas sensorerna. 
var rand = new Random();

// Variabler för loop och lista för devices. 
var lastRefresh = DateTimeOffset.MinValue;
List<Device> devices = new();

// Huvudloop - hämtar devices, skickar mätvärden. 
// Simulerar data och skickar meddelanden. 
while (true)
{
    if ((DateTimeOffset.UtcNow - lastRefresh) > TimeSpan.FromMinutes(1) || devices.Count == 0)
    {
        try
        {
            // Hämtar alla enheter/devices för denna tenant
            var latest = await http.GetFromJsonAsync<List<Device>>($"/api/tenants/{tenant.Id}/devices") ?? new();

            // Filtrera ut endast aktiva enheter/devices. 
            devices = latest.Where(d => string.Equals(d.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();
            System.Console.WriteLine($"Loaded {devices.Count} active devices for {tenantSlug}.");
            lastRefresh = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Failed to refresh devices: {ex.Message}");
        }
    }
    
    // skicka mätvärde per device
    foreach(var d in devices)
    {
        var metricType = InferMetricType(d);

        object[] metrics = metricType switch
        {
            "co2" => new object[] { new { type = "co2", value = 900 + rand.Next(0, 700), unit = "ppm" } },
            "temperature" => new object[] { new { type = "temperature", value = 21.5 + rand.NextDouble(), unit = "C" } },
            "humidity" => new object[] { new { type = "humidity", value = 30 + rand.NextDouble() * 30, unit = "%" } },
            "light" => new object[] { new { type = "light", value = 300 + rand.Next(0, 600), unit = "lm" } },
            "motion" => new object[] { new { type = "motion", value = rand.Next(0,2) == 1, unit = "" } },
            _ => Array.Empty<object>()
        };

        // Hoppar över devices utan metrics
        if (metrics.Length == 0) continue;

        // Skapa payload som ska skickas
        var payload = new
        {
            deviceId = d.Serial, // matchar DeviceRegistry.serial
            apiKey = $"{d.Serial}-key", // endast om ingest validerar nycklar.
            timeStamp = DateTimeOffset.UtcNow,
            metrics
        };

        // MQTT-topic som meddelandet skickas till måste matcha Ingest.GateWay.
        // Dynamisk MQTT topic
        var topic = "tenants/innovia/devices/dev-101/measurements";
        var json = JsonSerializer.Serialize(payload);

        // Skapa MQTT meddelande. 
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .Build();

        // Försöker publicera meddelandet 
        try
        {
            // Skickar meddelandet till MQTT. 
            await mqtt.PublishAsync(message);
            Console.WriteLine($"[{DateTimeOffset.UtcNow:o}] {d.Serial} ({metricType}) => {json}");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Publish failed for {d.Serial}: {ex.Message}");
        }

        // Liten delay mellan devices för att sprida meddelandena. 
        await Task.Delay(150);
    }

    


    //Här ställer man in hur många sekunder innan nästa meddelande ska skickas iväg. 
    await Task.Delay(TimeSpan.FromSeconds(10));
}


// Här definieras modellerna.
// Dessa klassen eller model var jag tvungen att skapa efter jag anropat Tenant och det är möjligt tack vare .net 5. 
// Detta är tenant modell och ser exakt likadan ut i diviceRegistry.
record Tenant(Guid Id, string Name, string Slug);
// Divice-Model denna matchar också DeviceRegistry API. 
record Device(Guid Id, Guid TenantId, Guid? RoomId, string Model, string Serial, string Status);