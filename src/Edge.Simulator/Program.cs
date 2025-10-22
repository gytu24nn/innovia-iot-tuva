using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using System.Text;
using System.Net.Http.Json;
using System.Data.Common;
using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using System.Reflection.Metadata.Ecma335;

// läser in konfiguration från JSON
var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
var configJson = await File.ReadAllTextAsync(configPath);
var config = JsonSerializer.Deserialize<SimulatorConfig>(configJson) ?? throw new Exception("Falied to load config.json");

System.Console.WriteLine("config loaded:");
System.Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions {WriteIndented = true}));

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

// Här skapas en random för att kunna få ut random data till låsas sensorerna. 
var rand = new Random();
var motionCounts = new Dictionary<string, int>();

// någon metod för att sätta typer. 
/* 
Den här metoden tar in en sträng modellnamn och försöker då identifiera vilken typ det är. 
Den normaliserar texten genom att göra den till lowercase, delar upp den i ord och jämför då mot de kända sensortyperna.
Efter de returnerar den en sträng som representerar den "standardiserade" sensortypen ex co2.
Om ingen då matchar den typen som är standardiserad blir det okänd. 
För att göra det så dynamiskt som möjligt. 
Använder mig nu av en config.json fil för att hämta knownTypes.
*/
string NormalizeType(string model)
{
    var words = Regex.Matches(model.ToLowerInvariant(), @"\w+").Select(x => x.Value);

    foreach (var word in words)
    {
        if (config.KnownTypes.TryGetValue(word, out var type)) return type;
    }

    return "Okänd";
};

// Bestämmer unit metod
/*
Den här metoden tar emot sensortypen från föra metoden tex co2 och returnerar då rätt unit. 
Dessa unit används sen när mätvärde skickas till frontend och då skrivs unit också ut samtidigt. 
Om det då inte finns något typ returneras unit som standard för att göra de så dynamiskt och användarvänligt som möjligt.
Använder mig nu av en config.json fil för att hämta units för att göra de mer dynamiskt. 
*/
string InferUnit(string type) {
    return config.Units.TryGetValue(type.ToLowerInvariant(), out var unit) ? unit : "unit";
}


/*
Denna metoden generar simulerade slumpmässiga mätvärden för en viss enhet.
Därför den innehåller en foreach loop för att den ska göra de för varje enhet/device och inte bara än.
Metoden använder också här sensortyperna som anges i enhetens model fält och skapar värden för varje typ.
Alla typer har olika metoder nästan men för att göra de då dynamiskt för att alla kanske inte har just dessa modeller.
Valde jag att göra okända typer får ett generiskt slumpvärde mellan 0-100 detta är inte super dynamiskt men bättre än vad min kod gjorde innan. 
Resultatet returneras som en array av objekt där varje objekt innehåller sensortyp, värde och enhet. 
*/
object[] GenerateMetrics(Device device)
{
    if (string.IsNullOrEmpty(device.Model)) return Array.Empty<object>();

    var types = device.Model.Split(',', StringSplitOptions.RemoveEmptyEntries);
    var metrics = new List<object>();

    foreach (var type in types)
    {
        var t = NormalizeType(type.Trim());
        double value;

        // Om värdeintervall finns i config.json, använd det
        if (config.ValueRanges.TryGetValue(t.ToLowerInvariant(), out var range))
        {
            value = range.min + rand.NextDouble() * (range.max - range.min);
        }
        // Specialhantering för rörelse (motion)
        else if (t == "motion")
        {
            value = motionCounts.TryGetValue(device.Serial, out var count)
                ? (rand.NextDouble() < 0.3 ? motionCounts[device.Serial] = count + 1 : count)
                : (motionCounts[device.Serial] = 0);
        }
        // Annars generiskt slumpvärde
        else
        {
            value = rand.NextDouble() * 100;
        }

        metrics.Add(new { type = t, value, unit = InferUnit(t) });
    }

    return metrics.ToArray();
}

// Variabler för loop och lista för devices. 
var lastRefresh = DateTimeOffset.MinValue;
List<Device> devices = new();

// Huvudloop - hämtar devices, skickar mätvärden. 
// Simulerar data och skickar meddelanden. 
while (true)
{
    if ((DateTimeOffset.UtcNow - lastRefresh) > TimeSpan.FromMinutes(config.refreshMinutes) || devices.Count == 0)
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
        var metrics = GenerateMetrics(d);
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
        var topic = $"tenants/{tenantSlug}/devices/{d.Serial}/measurements";
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
            Console.WriteLine($"[{DateTimeOffset.UtcNow:o}] {d.Serial} ({metrics}) => {json}");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"Publish failed for {d.Serial}: {ex.Message}");
        }

        // Liten delay mellan devices för att sprida meddelandena. 
        await Task.Delay(150);
    }

    


    //Här ställer man in hur många sekunder innan nästa meddelande ska skickas iväg. 
    await Task.Delay(TimeSpan.FromSeconds(config.intervalSeconds));
}


// Här definieras modellerna.
// Dessa klassen eller model var jag tvungen att skapa efter jag anropat Tenant och det är möjligt tack vare .net 5. 
// Detta är tenant modell och ser exakt likadan ut i diviceRegistry.
record Tenant(Guid Id, string Name, string Slug);
// Divice-Model denna matchar också DeviceRegistry API. 
record Device(Guid Id, Guid TenantId, Guid? RoomId, string Model, string Serial, string Status);

// model för vad som finns i config. 
record SimulatorConfig(
    Dictionary<string, string> KnownTypes,
    Dictionary<string, string> Units,
    Dictionary<string, Range> ValueRanges,
    int intervalSeconds,
    int refreshMinutes
);

record Range(double min, double max);