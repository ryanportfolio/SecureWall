using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PromptWall.NetworkProbe
{
    internal static class Program
    {
        private const int UsageError = 64;
        private const int InvalidArgument = 65;
        private const int TimedOut = 2;
        private const int ConnectionFailed = 3;

        private static int Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.Error.WriteLine("Usage: PromptWall.NetworkProbe.exe <IP address> <port> <timeout-ms>");
                return UsageError;
            }

            if (!IPAddress.TryParse(args[0], out IPAddress? address) ||
                !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
                port < 1 || port > 65535 ||
                !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int timeoutMs) ||
                timeoutMs < 100 || timeoutMs > 120000)
            {
                Console.Error.WriteLine("Invalid address, port, or timeout.");
                return InvalidArgument;
            }

            try
            {
                using var client = new TcpClient(address.AddressFamily);
                var connect = client.ConnectAsync(address, port);
                if (!connect.Wait(timeoutMs))
                {
                    Console.Error.WriteLine($"Timed out connecting to {address}:{port}.");
                    return TimedOut;
                }

                connect.GetAwaiter().GetResult();
                if (!client.Connected)
                {
                    Console.Error.WriteLine($"Connection to {address}:{port} did not complete.");
                    return ConnectionFailed;
                }

                Console.WriteLine($"Connected to {address}:{port}.");
                return 0;
            }
            catch (Exception exception) when (
                exception is SocketException ||
                exception is AggregateException)
            {
                Console.Error.WriteLine($"Connection to {address}:{port} failed: {exception.GetBaseException().Message}");
                return ConnectionFailed;
            }
        }
    }
}
