using System;

namespace pylorak.TinyWall
{
    // SecureWall's own persistent sublayers. These must never equal TinyWall's keys:
    // a TinyWall leftover would otherwise share, block or outlive SecureWall's sublayers.
    public static class WfpSublayerKeys
    {
        public static readonly Guid FWPM_LAYER_OUTBOUND_ICMP_ERROR_V6 =         new("{056B14B0-3DFB-4AE8-A3C4-45D7A69B4CD7}");
        public static readonly Guid FWPM_LAYER_OUTBOUND_ICMP_ERROR_V4 =         new("{FC959C7F-CE10-4F1B-9EDA-397CF26ABDA1}");
        public static readonly Guid FWPM_LAYER_INBOUND_ICMP_ERROR_V6 =          new("{E963FA95-59B4-453B-B268-F697A6215E40}");
        public static readonly Guid FWPM_LAYER_INBOUND_ICMP_ERROR_V4 =          new("{7639C5E7-5AE3-45E6-BB1E-3D0F0C02BADA}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 =            new("{FE37CC05-AD30-490C-A0EC-6B36320D4C74}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 =            new("{971743B2-42DA-4E22-A3D7-D49CB57A946B}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_LISTEN_V6 =             new("{55B09574-DB43-46FA-AF46-960544DF9822}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_LISTEN_V4 =             new("{6A2183A2-7C90-448E-9FCB-B14FE0AD2923}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 =        new("{1B2B3E33-F891-48E8-BF69-BA5DEC73AA08}");
        public static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 =        new("{85134B3F-F5DF-4FC1-ABCE-3B574B39EFDC}");
        public static readonly Guid FWPM_LAYER_INBOUND_TRANSPORT_V6_DISCARD =   new("{9F8BFF9D-687C-47A5-B7DF-FD9135B31FE4}");
        public static readonly Guid FWPM_LAYER_INBOUND_TRANSPORT_V4_DISCARD =   new("{B7BFF4C4-D8C3-4BD9-B18F-43BF70625179}");
        public static readonly Guid FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V6 =     new("{F43CAEB4-EF1F-4CEE-A2BF-C805F21296D8}");
        public static readonly Guid FWPM_LAYER_ALE_RESOURCE_ASSIGNMENT_V4 =     new("{D667F127-F4D7-4D45-A917-2E7509CBC3F4}");

        // TinyWall 3.x sublayer keys, also used by SecureWall builds up to v0.3.0.
        // SecureWall only reads these, and deletes them only when its own provider owns them.
        public static readonly Guid[] Legacy =
        {
            new(0x745777F7, 0x5092, 0x4706, 0x95, 0xC1, 0xB7, 0x4A, 0x00, 0x92, 0xE7, 0x8A),
            new(0x0FAC82A5, 0x64D6, 0x41CC, 0xBC, 0x5F, 0x41, 0x57, 0x02, 0xFF, 0xB6, 0x64),
            new(0xB06F3846, 0x5886, 0x4416, 0xA9, 0xF4, 0x6D, 0xCB, 0x8A, 0x9B, 0x8F, 0xC8),
            new(0x7BF8AAAB, 0xB21D, 0x415F, 0xBD, 0x03, 0xD2, 0x72, 0xFA, 0x37, 0x28, 0xEC),
            new(0xA6F49767, 0x7190, 0x4718, 0xA8, 0x0C, 0x9E, 0x05, 0x8A, 0xBA, 0x1B, 0x60),
            new(0x3C6B5A3E, 0x7413, 0x4BA0, 0x8B, 0x2D, 0x28, 0xC3, 0x6E, 0x39, 0xFE, 0x52),
            new(0xA36CEEFC, 0x83EB, 0x44FC, 0xB7, 0x84, 0x6E, 0x64, 0xEB, 0x3A, 0x95, 0x66),
            new(0x19EC1DE9, 0x7B31, 0x49D2, 0xBA, 0x12, 0x83, 0x94, 0x5E, 0x08, 0xF1, 0x68),
            new(0xDE420F02, 0x5DA6, 0x43B5, 0x94, 0xC1, 0xF1, 0x93, 0xC4, 0x90, 0x9A, 0xA4),
            new(0x089B5CA9, 0x6AC2, 0x4B5E, 0x93, 0x9B, 0xF1, 0x81, 0x88, 0x16, 0x88, 0x4F),
            new(0xD05BD1D0, 0x1298, 0x4E86, 0xBB, 0x38, 0x30, 0x5E, 0x0B, 0xEF, 0xB1, 0x12),
            new(0x024A1067, 0x9549, 0x4949, 0x89, 0x16, 0x4B, 0xB6, 0xDD, 0xCD, 0xBD, 0x6A),
            new(0x26DA5F3F, 0x9E15, 0x417E, 0x81, 0xDD, 0x60, 0x1D, 0xCF, 0x17, 0x21, 0xBF),
            new(0xF2E24D17, 0xB1FA, 0x44C8, 0xA3, 0x97, 0xCD, 0x20, 0x6B, 0x60, 0xBF, 0x8F),
        };
    }
}
