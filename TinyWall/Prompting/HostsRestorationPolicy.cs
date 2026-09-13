using System;

namespace pylorak.TinyWall.Prompting
{
    // The I/O boundary must distinguish a missing backup from an unreadable one.
    internal static class HostsRestorationPolicy
    {
        internal static bool Enable(Func<bool> hasOriginal, Action saveOriginal, Action install, Action flush)
        {
            if (!hasOriginal()) saveOriginal();
            install();
            flush();
            return true;
        }

        internal static bool Disable(Func<bool> hasOriginal, Action restore, Action deleteOriginal, Action flush)
        {
            if (!hasOriginal()) return true;
            restore();
            // A failed restore (even after swapping content) must retain recovery data.
            deleteOriginal();
            flush();
            return true;
        }
    }
}
