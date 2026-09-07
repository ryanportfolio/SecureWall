namespace pylorak.TinyWall.Prompting
{
    // One persistent+boot-time permit in the recovery baseline. Pure description so
    // tests can pin the exact set without WFP types. Inbound selects ALE_AUTH_RECV_ACCEPT,
    // outbound selects ALE_AUTH_CONNECT; IsIPv6 selects the V6 or V4 layer.
    internal sealed record RecoveryPermitRule(
        string Name,
        bool IsIPv6,
        bool Inbound,
        byte IpProtocol,
        ushort? LocalPort,
        ushort? RemotePort);
}
