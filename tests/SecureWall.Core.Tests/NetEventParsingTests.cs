using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using pylorak.Windows.WFP;

namespace SecureWall.Core.Tests
{
    internal static class NetEventParsingTests
    {
        internal static IEnumerable<(string Name, Action Test)> Cases
        {
            get
            {
                yield return ("appId blob without terminator reads exactly size bytes", ExactSizeWithoutNull);
                yield return ("appId blob stops at embedded null", EmbeddedNull);
                yield return ("appId blob of size zero is empty", SizeZero);
                yield return ("appId blob with odd size drops the trailing byte", OddSize);
                yield return ("appId blob shorter than its string is truncated to size", SizeShorterThanString);
                yield return ("appId null pointer is empty", NullPointer);
            }
        }

        // Backs the blob with an exact-size native buffer so any read past `size`
        // lands outside the allocation instead of in slack that happens to be zero.
        private static string Read(byte[] bytes, uint size)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                return NetEventSubscription.ReadAppIdPath(buffer, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

        private static void ExactSizeWithoutNull()
        {
            const string path = @"\device\harddiskvolume3\windows\system32\svchost.exe";
            var bytes = Utf16(path);

            AssertEx.Equal(path, Read(bytes, (uint)bytes.Length));
        }

        private static void EmbeddedNull()
        {
            var bytes = Utf16("C:\\a.exe\0junk");

            AssertEx.Equal("C:\\a.exe", Read(bytes, (uint)bytes.Length));
        }

        private static void SizeZero()
        {
            var bytes = Utf16("C:\\a.exe");

            AssertEx.Equal(string.Empty, Read(bytes, 0));
        }

        private static void OddSize()
        {
            var bytes = Utf16("abcd");

            AssertEx.Equal("abc", Read(bytes, 7));
        }

        private static void SizeShorterThanString()
        {
            var bytes = Utf16("abcd");

            AssertEx.Equal("ab", Read(bytes, 4));
        }

        private static void NullPointer()
        {
            AssertEx.Equal(string.Empty, NetEventSubscription.ReadAppIdPath(IntPtr.Zero, 64));
        }
    }
}
