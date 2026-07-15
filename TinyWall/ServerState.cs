using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace pylorak.TinyWall
{
    [DataContract(Namespace = "TinyWall")]
    public class UpdateModule
    {
        [DataMember, AllowNull]
        public string Component;
        [DataMember]
        public string? ComponentVersion;
        [DataMember]
        public string? DownloadHash;
        [DataMember]
        public string? UpdateURL;
    }

    [DataContract(Namespace = "TinyWall")]
    public class UpdateDescriptor : ISerializable<UpdateDescriptor>
    {
        public static readonly string ISTALLER_ARCH_SUFFIX = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x86",   // Selects the 32-bit installer even on x64. This is intentional as long as Win32 is supported.
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException()
        };
        public static readonly string MODULE_NAME_MAINBIN = "TinyWall_" + ISTALLER_ARCH_SUFFIX;
        public const string MODULE_NAME_HOSTS = "HostsFile";
        public const string MODULE_NAME_DATABASE = "Database";

        [DataMember]
        public string MagicWord = "TinyWall Update Descriptor";
        [DataMember]
        public UpdateModule[] Modules = Array.Empty<UpdateModule>();

        public JsonTypeInfo<UpdateDescriptor> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.UpdateDescriptor;
        }

        public UpdateModule? GetModule(string moduleName)
        {
            for (int i = 0; i < Modules.Length; ++i)
            {
                if (Modules[i].Component.Equals(moduleName, StringComparison.InvariantCultureIgnoreCase))
                    return Modules[i];
            }

            return null;
        }
    }

    public class ServerState : ISerializable<ServerState>
    {
        public bool HasPassword = false;
        public bool Locked = false;
        public UpdateDescriptor? Update = null;
        public FirewallMode Mode = FirewallMode.Unknown;
        public List<MessageType> ClientNotifs = new();

        public JsonTypeInfo<ServerState> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ServerState;
        }
    }
}
