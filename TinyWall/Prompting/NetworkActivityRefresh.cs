using System;

namespace pylorak.TinyWall.Prompting
{
    internal static class NetworkActivityRefresh
    {
        internal static bool TryRun(Action refresh)
        {
            if (refresh == null)
                throw new ArgumentNullException(nameof(refresh));

            try
            {
                refresh();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
