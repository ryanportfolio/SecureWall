using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace pylorak.TinyWall
{
    // Host names for remote addresses, read from the Windows DNS client cache: a name some
    // program on this PC looked up for that address, not necessarily the one this connection
    // used (a shared CDN address can carry several). Reading the cache sends nothing on the
    // network. An address no program resolved through Windows DNS, or whose record has expired,
    // has no name. The cache also holds every hosts-file entry, so with a large blocklist one
    // read can take seconds; it runs on a background thread and at most every 30 seconds.
    internal sealed class DnsCacheNames
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
        private volatile Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _sinceRefresh = new();
        private int _refreshing;

        internal string? Lookup(IPAddress address)
        {
            address = Unmapped(address);
            if (!Nameable(address))
                return null;
            return _names.TryGetValue(address.ToString(), out string? name) ? name : null;
        }

        // UI thread. Starts a background read when the last one is older than the interval.
        internal void RefreshIfStale()
        {
            if ((_sinceRefresh.IsRunning && _sinceRefresh.Elapsed < RefreshInterval) || Interlocked.Exchange(ref _refreshing, 1) == 1)
                return;
            _sinceRefresh.Restart();
            Task.Run(() =>
            {
                try { _names = Read(); }
                catch (Exception) { }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }

        private static IPAddress Unmapped(IPAddress address) =>
            address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        // Hosts-file entries point blocked names at loopback or the unspecified address, and
        // broadcast and multicast addresses name no single host.
        private static bool Nameable(IPAddress address)
        {
            if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) ||
                IPAddress.Broadcast.Equals(address) || IPAddress.IsLoopback(address) || address.IsIPv6Multicast)
                return false;
            return address.AddressFamily != AddressFamily.InterNetwork || (address.GetAddressBytes()[0] & 0xF0) != 0xE0;
        }

        private static Dictionary<string, string> Read()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Type 1 is an A record, 28 an AAAA record.
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Entry, Data FROM MSFT_DNSClientCache WHERE Type = 1 OR Type = 28",
                new EnumerationOptions { Timeout = ReadTimeout, ReturnImmediately = true, Rewindable = false });
            using ManagementObjectCollection records = searcher.Get();
            foreach (ManagementBaseObject record in records)
            {
                using (record)
                {
                    if (record["Entry"] is string entry && record["Data"] is string data &&
                        IPAddress.TryParse(data, out IPAddress? parsed))
                    {
                        IPAddress address = Unmapped(parsed);
                        string key = address.ToString();
                        if (Nameable(address) && !names.ContainsKey(key))
                            names[key] = entry;
                    }
                }
            }
            return names;
        }
    }
}
