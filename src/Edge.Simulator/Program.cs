using MQTTnet;
using MQTTnet.Client;
using System.Text.Json;
using System.Text;

// Här skapas en klient som kan koppla upp sig mot MQTT-brokern. 
var factory = new MqttFactory();
var client = factory.CreateMqttClient();

// Här ställs anslutningen in så att den kopplar till rät localhost.
var options = new MqttClientOptionsBuilder()
    .WithTcpServer("localhost", 1883)
    .Build();

// Här kontrollerar man så att den är ansluten.
Console.WriteLine("Edge.Simulator starting… connecting to MQTT at localhost:1883");
try
{
    await client.ConnectAsync(options);
    Console.WriteLine("✅ Connected to MQTT broker.");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Failed to connect to MQTT broker: {ex.Message}");
    throw;
}

// Här skapas en random för att kunna få ut random data till låsas sensorerna. 
var rand = new Random();
// Simulerar data och skickar meddelanden. 
while (true)
{
    // Payload skickar ett meddelande som ser ut som en sensor skickat de. 
    var payload = new
    {
        deviceId = "dev-101",
        apiKey = "dev-101-key",
        timestamp = DateTimeOffset.UtcNow,
        // Värderna från "sensorerna".
        metrics = new object[]
        {
            new { type = "temperature", value = 21.5 + rand.NextDouble(), unit = "C" },
            new { type = "co2", value = 900 + rand.Next(0, 700), unit = "ppm" }
        }
    };

    // MQTT-topic som meddelandet skickas till måste matcha Ingest.GateWay.
    var topic = "tenants/innovia/devices/dev-101/measurements";
    var json = JsonSerializer.Serialize(payload);

    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(Encoding.UTF8.GetBytes(json))
        .Build();

    // Skickar meddelandet till MQTT. 
    await client.PublishAsync(message);
    Console.WriteLine($"[{DateTimeOffset.UtcNow:o}] Published to '{topic}': {json}");
    //Här ställer man in hur många sekunder innan nästa meddelande ska skickas iväg. 
    await Task.Delay(TimeSpan.FromSeconds(10));
}
