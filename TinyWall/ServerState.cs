using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;

namespace pylorak.TinyWall
{
    public class ServerState : ISerializable<ServerState>
    {
        public bool HasPassword = false;
        public bool Locked = false;
        public FirewallMode Mode = FirewallMode.Unknown;
        public bool AttributionAvailable = false;
        public long DroppedPromptCandidates = 0;
        public long DroppedPrompts = 0;
        public Prompting.ServiceHealthWarning HealthWarnings = Prompting.ServiceHealthWarning.None;
        public List<MessageType> ClientNotifs = new();

        public JsonTypeInfo<ServerState> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ServerState;
        }
    }
}
