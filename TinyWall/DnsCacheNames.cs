using System;
using System.Collections.Generic;
using System.Management;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace pylorak.TinyWall
{
    // Host names for remote addresses, read from the Windows DNS client cache: the names
    // programs on this PC looked up recently. Reading the cache sends nothing on the network.
    // An address no program resolved through Windows DNS, or whose record has expired, has no
    // name. The cache also holds every hosts-file entry, so with a large blocklist one read can
    // take seconds; it runs on a background thread and at most every 30 seconds.
    internal sealed class DnsCacheNames
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
        private volatile Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRefreshUtc = DateTime.MinValue;
        private int _refreshing;

        internal string? Lookup(IPAddress address)
        {
            if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.IsLoopback(address))
                return null;
            return _names.TryGetValue(Key(address), out string? name) ? name : null;
        }

        // UI thread. Starts a background read when the last one is older than the interval.
        internal void RefreshIfStale()
        {
            if (DateTime.UtcNow - _lastRefreshUtc < RefreshInterval || Interlocked.Exchange(ref _refreshing, 1) == 1)
                return;
            _lastRefreshUtc = DateTime.UtcNow;
            Task.Run(() =>
            {
                try { _names = Read(); }
                catch (Exception) { }
                finally { Volatile.Write(ref _refreshing, 0); }
            });
        }

        private static string Key(IPAddress address) =>
            (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

        private static Dictionary<string, string> Read()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Type 1 is an A record, 28 an AAAA record.
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2",
                "SELECT Entry, Data FROM MSFT_DNSClientCache WHERE Type = 1 OR Type = 28");
            using ManagementObjectCollection records = searcher.Get();
            foreach (ManagementBaseObject record in records)
            {
                using (record)
                {
                    if (record["Entry"] is string entry && record["Data"] is string data &&
                        IPAddress.TryParse(data, out IPAddress? address))
                    {
                        string key = Key(address);
                        if (!names.ContainsKey(key))
                            names[key] = entry;
                    }
                }
            }
            return names;
        }
    }
}
