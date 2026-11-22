
using System.Text.Json.Serialization;

namespace SmartParking.Api.Dtos;

public class MqttSlotDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("occupied")]
    public bool Occupied { get; set; }

    [JsonPropertyName("sessionId")]
    public int SessionId { get; set; }

    [JsonPropertyName("reserved")]
    public bool Reserved { get; set; }

    [JsonPropertyName("virtual")]
    public bool Virtual { get; set; }

    [JsonPropertyName("distance")]
    public decimal? Distance { get; set; }
}

public class MqttSessionDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("plate")]
    public string Plate { get; set; } = "";

    [JsonPropertyName("owner")]
    public string Owner { get; set; } = "";

    [JsonPropertyName("entryTime")]
    public long EntryTime { get; set; }

    [JsonPropertyName("currentFee")]
    public decimal CurrentFee { get; set; }

    [JsonPropertyName("spots")]
    public List<string> Spots { get; set; } = new();

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("reservedSpot")]
    public string ReservedSpot { get; set; } = "";
}
