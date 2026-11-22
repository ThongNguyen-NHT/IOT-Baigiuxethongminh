using System.Collections.Generic;

namespace SmartParking.Api.Configuration;

public class BackupBrokerConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
}

public class MqttSettings
{
    public string Host { get; set; } = "broker.hivemq.com";
    public int Port { get; set; } = 1883;
    public int TlsPort { get; set; } = 8883;
    public List<BackupBrokerConfig> BackupBrokers { get; set; } = new();
    public string ClientId { get; set; } = $"smartparking-backend-{Guid.NewGuid():N}";
    public string TopicPrefix { get; set; } = "dacn-smartparking/lot1";
    public bool UseTls { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
