namespace pylorak.TinyWall.Prompting
{
    internal static class PromptFilterClassifier
    {
        internal static bool IsRequiredDefaultBlock(
            bool isBlock,
            ulong filterWeight,
            ulong defaultBlockWeight)
        {
            return isBlock && filterWeight == defaultBlockWeight;
        }

        internal static bool IsPromptable(
            bool isBlock,
            ulong filterWeight,
            ulong defaultBlockWeight,
            bool isOutboundAleConnectLayer)
        {
            return IsRequiredDefaultBlock(isBlock, filterWeight, defaultBlockWeight) &&
                isOutboundAleConnectLayer;
        }
    }
}
