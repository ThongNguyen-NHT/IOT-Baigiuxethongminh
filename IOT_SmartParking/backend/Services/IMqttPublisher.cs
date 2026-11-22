namespace SmartParking.Api.Services;

public interface IMqttPublisher
{
    Task PublishAsync(string topic, string payload, bool retain = false);
    bool IsConnected { get; }
}
