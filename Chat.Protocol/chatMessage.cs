
namespace Chat.Protocol;

public sealed class ChatMessage
{
    public string Type { get; set; } = "";
    public string RequestId { get; set; } = "";
    public System.Text.Json.JsonElement Payload { get; set; }
}
