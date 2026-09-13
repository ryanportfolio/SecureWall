using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class PromptTransactionIntegrationTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("production prompt transaction commits Allow and revokes other tokens", SuccessfulAllow),
        ("production prompt transaction restores failed Allow and permits same-token retry", RecoverableAllow),
        ("production prompt transaction rollback failure revokes tokens permanently", FailedRollback),
        ("production prompt mode commit rejects stale tokens", ModeChange),
        ("production prompt shutdown during failed Allow never restores token", Shutdown),
        ("production prompt recoverable failure does not extend token lifetime", Expiry),
        ("production prompt service wires invalidation before replacement and revocation after commit", ServiceWiring),
    };

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PolicyFixture
    {
        internal readonly Clock Clock = new();
        internal readonly PromptQueue Queue;
        internal readonly CorrelatedDropBatch Batches;
        internal readonly DropCandidateBuffer Candidates;
        internal bool Restored, Published, Stopped;
        internal PolicyFixture()
        {
            Queue = new PromptQueue(Clock);
            Batches = new CorrelatedDropBatch(Queue.SyncRoot, Clock);
            Candidates = new DropCandidateBuffer(Clock);
        }
        internal Guid Add(string name) => Queue.Enqueue(PromptIdentity.ForExecutable(@"C:\fixture\" + name), "203.0.113.1", 443, 6).Token;
        internal void Reset(bool stop = false, bool revokeTokens = true) =>
            PromptCandidateLifecycle.Reset(Queue, Batches, Candidates, stop, revokeTokens);
        internal void Stop() { Stopped = true; Reset(stop: true); }
        // Same production transaction, queue and reset boundaries as ApplyConfiguration
        // and InstallFirewallRules. Only storage/WFP work is replaced with delegates.
        internal void Apply(Action nativeReplace, bool rollbackFails = false)
        {
            PolicyChangeTransaction.Apply(() => { }, () =>
            {
                Reset(revokeTokens: false);
                nativeReplace();
                Reset(); // committed WFP policy
            }, () =>
            {
                if (rollbackFails) throw new IOException("restore");
                Restored = true;
            }, () => { Reset(); Published = true; }, Stop);
        }
        internal PromptActionResult Allow(Guid token, Action replace, bool rollbackFails = false) =>
            Queue.Allow(token, _ => { Apply(replace, rollbackFails); return true; });
    }

    private static void SuccessfulAllow()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe"), other = f.Add("two.exe");
        AssertEx.Equal(PromptActionStatus.Allowed, f.Allow(token, () => { }).Status);
        AssertEx.True(f.Published);
        AssertEx.Equal(0, f.Queue.GetPending().Count);
        AssertEx.Equal(PromptActionStatus.UnknownToken, f.Queue.Allow(other).Status);
    }

    private static void RecoverableAllow()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe"), other = f.Add("two.exe");
        var before = f.Queue.GetPending()[0];
        long generation = f.Batches.Generation;
        AssertEx.Equal(PromptActionStatus.ApplyFailed, f.Allow(token, () => throw new IOException("WFP replacement")).Status);
        AssertEx.True(f.Restored && !f.Published && !f.Stopped);
        AssertEx.Equal(2, f.Queue.GetPending().Count);
        AssertEx.Equal(before.ExpiresUtc, f.Queue.GetPending()[0].ExpiresUtc);
        AssertEx.True(f.Batches.Generation > generation);
        bool stalePublished = false;
        f.Batches.Publish(generation, () => stalePublished = true);
        AssertEx.False(stalePublished);
        AssertEx.Equal(PromptActionStatus.Allowed, f.Allow(token, () => { }).Status);
        AssertEx.Equal(PromptActionStatus.UnknownToken, f.Queue.Allow(other).Status);
    }

    private static void FailedRollback()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe");
        AssertEx.Equal(PromptActionStatus.ApplyFailed, f.Allow(token, () => throw new IOException("WFP"), true).Status);
        AssertEx.True(f.Stopped && !f.Published);
        f.Reset(revokeTokens: false);
        AssertEx.Equal(PromptActionStatus.UnknownToken, f.Queue.Allow(token).Status);
        AssertEx.False(f.Batches.TryAccept(() => throw new Exception("stopped callback")));
    }

    private static void ModeChange()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe");
        f.Apply(() => { });
        AssertEx.True(f.Published);
        AssertEx.Equal(PromptActionStatus.UnknownToken, f.Queue.Allow(token).Status);
    }

    private static void Shutdown()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe");
        AssertEx.Equal(PromptActionStatus.ApplyFailed, f.Allow(token, () => { f.Stop(); throw new IOException("shutdown"); }).Status);
        AssertEx.True(f.Restored);
        f.Reset(revokeTokens: false);
        bool published = false;
        f.Batches.Publish(f.Batches.Generation, () => published = true);
        AssertEx.False(published);
        AssertEx.Equal(PromptActionStatus.UnknownToken, f.Queue.Allow(token).Status);
    }

    private static void Expiry()
    {
        var f = new PolicyFixture();
        Guid token = f.Add("one.exe");
        f.Allow(token, () => { f.Clock.UtcNow += TimeSpan.FromMinutes(3); throw new IOException("slow replacement"); });
        AssertEx.Equal(PromptActionStatus.Expired, f.Queue.Allow(token).Status);
    }

    internal static string Source(string path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TinyWall", "TinyWallService.cs"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Repository source not found.");
        return File.ReadAllText(Path.Combine(dir.FullName, path)).Replace("\r\n", "\n");
    }

    private static void ServiceWiring()
    {
        string service = Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("private void InstallFirewallRules()", StringComparison.Ordinal);
        int invalidate = service.IndexOf("ResetPromptCandidates(revokeTokens: false);", start, StringComparison.Ordinal);
        int rebuild = service.IndexOf("PathMapper.Instance.RebuildCache();", start, StringComparison.Ordinal);
        int commit = service.IndexOf("trx.Commit();", start, StringComparison.Ordinal);
        int revoke = service.IndexOf("ResetPromptCandidates();", start, StringComparison.Ordinal);
        AssertEx.True(start < invalidate && invalidate < rebuild && rebuild < commit && commit < revoke);
        AssertEx.True(service.Contains("PromptCandidateLifecycle.Reset(BlockedPromptQueue, CorrelatedDrops, DropCandidates, stop, revokeTokens);"));
        AssertEx.True(service.Contains("ResetPromptCandidates();\n                            VisibleState.Mode = mode;"));
        foreach (string method in new[] { "private void FailClosed()", "public void Dispose()" })
        {
            int at = service.IndexOf(method, StringComparison.Ordinal);
            AssertEx.True(at >= 0 && service.IndexOf("ResetPromptCandidates(stop: true);", at, StringComparison.Ordinal) > at);
        }
    }
}
