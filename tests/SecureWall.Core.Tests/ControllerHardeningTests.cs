using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

internal static class ControllerHardeningTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("pipe SCM configuration requires exact quoted image LocalSystem and own process", PipeServerIdentityMustMatch),
        ("display snapshots remove withdrawn current and queued prompts", SnapshotsRemoveWithdrawn),
        ("display drops expired snapshots and bounds live backlog", DisplayBoundsBacklog),
        ("display expiry continues while polling is stalled", TickExpiresCurrentAndQueue),
        ("allow click at expiry never sends an allow", ExpiredAllowNeverSent),
        ("failed automatic dismissal closes once and cannot reappear in polling", FailedTimeoutClosesOnce),
        ("stalled automatic dismissal cannot delay local expiry", StalledTimeoutDoesNotDelayExpiry),
        ("late allow completion cannot close a replacement popup", LateAllowCannotChangeReplacement),
        ("popup stays until token expiry and no longer", PopupStaysUntilTokenExpiry),
        ("allow arms only after one stable second and a finished risk probe", AllowArmingNeedsDelayAndRiskProbe),
        ("popup shows how many prompts wait behind it", PopupShowsWaitingCount),
        ("expired and revoked prompts go to the missed list, answered ones do not", UnansweredPromptsAreMissed),
        ("a live prompt replaces its missed entry and the list is bounded", MissedListDedupesAndBounds),
        ("allow on an expired or revoked token keeps the prompt in the missed list", FailedAllowIsMissed),
        ("always allow sends its own request for the same token and completes the prompt", AlwaysAllowUsesItsRequest),
        ("always allow rules match any folder by file name and only the exact signer", AlwaysAllowRuleMatching),
        ("only a granted always allow counts as owner activity", AlwaysAllowActivity),
        ("locked allow unlocks retries the same token and relocks", LockedAllowUnlocksAndRelocks),
        ("cancelled popup unlock keeps the prompt blocked and open", CancelledUnlockKeepsPromptOpen),
        ("allow after an unlock elsewhere retries without relocking", AlreadyUnlockedRetriesWithoutRelock),
        ("lost or failed unlock response still relocks", LostUnlockResponseStillRelocks),
        ("unlock outlasting the token never sends a second allow", UnlockPastExpiryNeverAllows),
        ("prompt withdrawn during unlock still relocks", WithdrawnDuringUnlockStillRelocks),
        ("AI endpoints require HTTPS and reject credentials query and fragment", AiRejectsUnsafeUrls),
        ("AI default payload identifies all disclosed fields without path or destination", AiPayloadDisclosure),
        ("audit lease ownership cannot mask subscription loss or disposal", AuditHealth),
        ("concurrent overflow counters retain counts and bound reports", DiagnosticCounts),
        ("attribution and overload notifications share a bounded budget", DiagnosticNotifications),
        ("admission contention is coalesced locally without overflow notifications", ContentionDiagnostics),
    };

    private static readonly DateTimeOffset Start = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static void AuditHealth()
    {
        var health = new AuditSubscriptionHealth();
        Check(!health.Available(true));
        health.Starting();
        Check(health.SubscriptionAvailable && !health.Available(false) && health.Available(true));
        health.Failed();
        Check(!health.Available(true));
        health.Starting();
        Check(health.Available(true));
        health.Stop();
        Parallel.For(0, 1000, _ => { health.Starting(); health.Failed(); });
        health.Starting();
        Check(health.Stopped && !health.SubscriptionAvailable && !health.Available(true));
    }

    private static void DiagnosticCounts()
    {
        var diagnostic = new CoalescedDiagnostic();
        Parallel.For(0, 10000, _ => diagnostic.Record());
        Check(diagnostic.Total == 10000);
        Check(diagnostic.TryReport(Start, out long count) && count == 10000);
        Parallel.For(0, 10000, _ => diagnostic.Record());
        for (int i = 0; i < 60; ++i)
            Check(!diagnostic.TryReport(Start.AddSeconds(i), out _));
        Check(diagnostic.TryReport(Start.AddMinutes(1), out count) && count == 10000);
        Check(!diagnostic.TryReport(Start.AddMinutes(2), out _) && diagnostic.Total == 20000);
    }

    private static void DiagnosticNotifications()
    {
        var gate = new AttributionNotificationGate();
        Check(gate.Update(true, 0, 0, Start) == null);
        Check(gate.Update(false, 0, 0, Start) == AttributionStatusText.Unavailable);
        for (int i = 1; i < 60; ++i)
            Check(gate.Update(i % 2 == 0, i, i, Start.AddSeconds(i)) == null);
        Check(gate.Update(false, 60, 60, Start.AddMinutes(1)) == AttributionStatusText.Unavailable);
        Check(gate.Update(false, 60, 60, Start.AddMinutes(2)) == AttributionStatusText.Overflow);
        Check(gate.Update(false, 60, 60, Start.AddMinutes(3)) == null);
        Check(AttributionStatusText.Unavailable.Contains("Firewall enforcement is unchanged"));
        Check(AttributionStatusText.Overflow.Contains("remain blocked"));
    }

    private static void ContentionDiagnostics()
    {
        object guard = new();
        var batch = new CorrelatedDropBatch(guard, new DiagnosticClock(), 1);
        var gate = new AttributionNotificationGate();
        lock (guard)
        {
            bool accepted = false;
            var callback = new Thread(() =>
            {
                for (int i = 0; i < 100; ++i) accepted |= batch.TryAccept(() => { });
            }) { IsBackground = true };
            callback.Start();
            Check(callback.Join(3000) && !accepted);
        }
        Check(batch.Contention.Total == 100 && batch.Suppressed.Total == 0);
        Check(batch.Contention.TryReport(Start, out long count) && count == 100);
        batch.Contention.Record();
        Check(!batch.Contention.TryReport(Start.AddSeconds(59), out _));
        Check(batch.Contention.TryReport(Start.AddMinutes(1), out count) && count == 1);
        Check(gate.Update(true, batch.Suppressed.Total, 0, Start) == null);
        var candidate = new DropCandidate(Start, 42, @"C:\app.exe", null,
            "192.0.2.1", 1234, "203.0.113.1", 443, 6);
        var audit = new BlockedConnectionAuditEvent(Start, 77, candidate.ApplicationPath,
            ConnectionDirection.Outbound, candidate.LocalAddress, 1234, candidate.RemoteAddress, 443, 6, 42, null);
        Check(batch.TryAdd(candidate, audit) && !batch.TryAdd(candidate, audit));
        Check(batch.Suppressed.Total == 1 && batch.Contention.Total == 101);
        Check(gate.Update(true, batch.Suppressed.Total, 0, Start) == AttributionStatusText.Overflow);
    }

    private sealed class DiagnosticClock : IClock
    {
        public DateTimeOffset UtcNow => Start;
    }

    private static void PipeServerIdentityMustMatch()
    {
        const string expected = @"C:\Program Files\SecureWall\SecureWall.exe";
        string command = "\"" + expected + "\"";
        Check(PipeServerAuthorization.IsExpectedConfiguration(command.ToUpperInvariant(), expected, "LocalSystem", 0x10));
        Check(PipeServerAuthorization.IsExpectedConfiguration(command + " /service", expected, @"NT AUTHORITY\SYSTEM", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(expected, expected, "LocalSystem", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration("\"C:\\temp\\SecureWall.exe\"", expected, "LocalSystem", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(command + " /selfhosted", expected, "LocalSystem", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(command, expected, "LocalService", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(command, expected, "LocalSystem", 0x20));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(null, expected, "LocalSystem", 0x10));
        Check(!PipeServerAuthorization.IsExpectedConfiguration(command, null, "LocalSystem", 0x10));
    }

    private static void SnapshotsRemoveWithdrawn()
    {
        var views = new List<View>();
        var actions = new Actions();
        using var display = new PromptDisplayCoordinator(actions, () => NewView(views), () => Start);
        var first = Prompt(Start.AddMinutes(1));
        var second = Prompt(Start.AddMinutes(1));
        var third = Prompt(Start.AddMinutes(1));
        display.Reconcile(new[] { first, second });
        display.Reconcile(new[] { third });
        Check(views[0].Closed && display.CurrentToken == third.Token && display.PendingCount == 0);
        display.Reconcile(Array.Empty<PromptWireDto>());
        Check(display.CurrentToken == null && views[1].Closed && actions.AllowCount == 0 && actions.DismissCount == 0);
    }

    private static void DisplayBoundsBacklog()
    {
        var views = new List<View>();
        using var display = new PromptDisplayCoordinator(new Actions(), () => NewView(views), () => Start);
        display.Enqueue(Enumerable.Range(0, 500).Select(_ => Prompt(Start.AddSeconds(-1))));
        Check(views.Count == 0 && display.PendingCount == 0);
        display.Enqueue(Enumerable.Range(0, 500).Select(_ => Prompt(Start.AddMinutes(1))));
        Check(views.Count == 1 && display.PendingCount == PromptDisplayCoordinator.MaximumDisplayedPrompts - 1);
        display.Reconcile(Enumerable.Range(0, 500).Select(_ => Prompt(Start.AddMinutes(1))));
        Check(display.PendingCount == PromptDisplayCoordinator.MaximumDisplayedPrompts - 1);
    }

    private static void TickExpiresCurrentAndQueue()
    {
        var now = Start;
        var views = new List<View>();
        var actions = new Actions();
        using var display = new PromptDisplayCoordinator(actions, () => NewView(views), () => now);
        var useful = Prompt(Start.AddMinutes(2));
        display.Enqueue(new[] { Prompt(Start.AddSeconds(20)), Prompt(Start.AddSeconds(20)), useful });
        now = Start.AddSeconds(20);
        display.Tick();
        Check(views[0].Closed && views.Count == 2 && display.CurrentToken == useful.Token && display.PendingCount == 0);
        Check(actions.AllowCount == 0 && actions.DismissCount == 0);
    }

    private static void ExpiredAllowNeverSent()
    {
        var now = Start;
        var view = new View();
        var actions = new Actions();
        using var display = new PromptDisplayCoordinator(actions, () => view, () => now);
        display.Enqueue(new[] { Prompt(Start.AddSeconds(1)) });
        now = Start.AddSeconds(1);
        view.Allow();
        Check(actions.AllowCount == 0 && view.Closed && display.CurrentToken == null);
    }

    private static void AlwaysAllowUsesItsRequest()
    {
        var actions = new Actions();
        var views = new List<View>();
        using var display = new PromptDisplayCoordinator(actions, () => NewView(views), () => Start);
        var prompt = Prompt(Start.AddMinutes(2));
        display.Reconcile(new[] { prompt });
        views[0].AlwaysAllow();
        Check(actions.AllowAlwaysCount == 1 && actions.AllowCount == 0 && actions.AllowTokens.Single() == prompt.Token);
        Check(views[0].Closed && display.CurrentToken == null && display.MissedPrompts.Count == 0);

        // A locked service: the same Always allow is retried after the unlock, then relocked.
        var locked = new Actions { UnlockResult = PromptUnlockResult.Unlocked };
        locked.AllowResults.Enqueue(PromptActionStatus.Locked);
        var lockedViews = new List<View>();
        using var lockedDisplay = new PromptDisplayCoordinator(locked, () => NewView(lockedViews), () => Start);
        lockedDisplay.Reconcile(new[] { Prompt(Start.AddMinutes(2)) });
        lockedViews[0].AlwaysAllow();
        Check(locked.AllowAlwaysCount == 2 && locked.AllowCount == 0 && locked.RelockCount == 1 && lockedViews[0].Closed);
    }

    private static void AlwaysAllowRuleMatching()
    {
        const string anthropic = "CN=\"Anthropic, PBC\", O=\"Anthropic, PBC\", C=US";
        var rule = new AlwaysAllowRule("claude.exe", anthropic, "Anthropic, PBC");
        Check(rule.NameMatches(@"C:\Users\me\AppData\Local\AnthropicClaude\app-4.29.0\claude.exe"));
        Check(rule.NameMatches(@"C:\Users\me\.local\bin\CLAUDE.EXE"));
        Check(!rule.NameMatches(@"C:\Users\me\Downloads\claude.exe.bak"));
        Check(!rule.NameMatches(@"C:\Users\me\Downloads\notclaude.exe"));
        Check(rule.Matches(@"D:\anywhere\claude.exe", anthropic));
        Check(!rule.Matches(@"D:\anywhere\claude.exe", "CN=\"Anthropic, PBC\", O=Someone Else"));
        Check(!rule.Matches(@"D:\anywhere\claude.exe", anthropic.ToLowerInvariant()));
        Check(!new AlwaysAllowRule("claude.exe", string.Empty, string.Empty).NameMatches(@"C:\x\claude.exe"));
        Check(!new AlwaysAllowRule(string.Empty, anthropic, "Anthropic, PBC").Matches(@"C:\x\claude.exe", anthropic));

        var rules = new List<AlwaysAllowRule>();
        Check(AlwaysAllowRule.AddTo(rules, rule));
        Check(!AlwaysAllowRule.AddTo(rules, new AlwaysAllowRule("CLAUDE.exe", anthropic, "other display name")));
        Check(AlwaysAllowRule.AddTo(rules, new AlwaysAllowRule("claude.exe", "CN=Other", "Other")));
        Check(rules.Count == 2);
    }

    private static void AlwaysAllowActivity()
    {
        foreach (PromptActionStatus status in Enum.GetValues<PromptActionStatus>())
        {
            bool counted = ControllerActivityPolicy.IsUserActivity(MessageType.ALLOW_PROMPT_ALWAYS, MessageType.ALLOW_PROMPT_ALWAYS, status);
            Check(counted == (status == PromptActionStatus.Allowed));
        }
        Check(!ControllerActivityPolicy.IsUserActivity(MessageType.ALLOW_PROMPT_ALWAYS, MessageType.ALLOW_PROMPT, PromptActionStatus.Allowed));
        Check(!ControllerActivityPolicy.IsUserActivity(MessageType.ALLOW_PROMPT_ALWAYS, MessageType.RESPONSE_LOCKED, null));
    }

    private static void PopupStaysUntilTokenExpiry()
    {
        var deadline = new PromptDisplayDeadline(Start.AddMinutes(2));
        Check(!deadline.ShouldClose(Start.AddSeconds(30)) && !deadline.ShouldClose(Start.AddSeconds(119)));
        Check(deadline.ShouldClose(Start.AddMinutes(2)) && deadline.ShouldClose(Start.AddMinutes(3)));
    }

    private static void PopupShowsWaitingCount()
    {
        var views = new List<View>();
        using var display = new PromptDisplayCoordinator(new Actions(), () => NewView(views), () => Start);
        var first = Prompt(Start.AddMinutes(2));
        var second = Prompt(Start.AddMinutes(2), @"C:\apps\second.exe");
        var third = Prompt(Start.AddMinutes(2), @"C:\apps\third.exe");
        display.Reconcile(new[] { first, second, third });
        Check(views.Count == 1 && views[0].Waiting == 2);
        display.Reconcile(new[] { first, second });
        Check(views[0].Waiting == 1);
        views[0].Ignore();
        Check(views.Count == 2 && views[1].Waiting == 0);
    }

    private static void UnansweredPromptsAreMissed()
    {
        var now = Start;
        var views = new List<View>();
        var added = new List<PromptWireDto>();
        using var display = new PromptDisplayCoordinator(new Actions(), () => NewView(views), () => now);
        display.MissedPromptsChanged += (_, e) => added.AddRange(e.Added);
        var allowed = Prompt(Start.AddMinutes(2), @"C:\apps\allowed.exe");
        var ignored = Prompt(Start.AddMinutes(2), @"C:\apps\ignored.exe");
        var revoked = Prompt(Start.AddMinutes(2), @"C:\apps\revoked.exe");
        var expired = Prompt(Start.AddMinutes(1), @"C:\apps\expired.exe");
        display.Reconcile(new[] { allowed, ignored, revoked, expired });
        views[0].Allow();
        views[1].Ignore();
        Check(display.MissedPrompts.Count == 0 && added.Count == 0);
        // A policy change revoked the shown prompt; the queued one expires unseen.
        display.Reconcile(new[] { expired });
        Check(display.MissedPrompts.Count == 1 && display.MissedPrompts[0].Token == revoked.Token);
        now = Start.AddMinutes(1);
        display.Tick();
        Check(display.MissedPrompts.Count == 2 && display.MissedPrompts[0].Token == expired.Token);
        Check(added.Select(p => p.Token).SequenceEqual(new[] { revoked.Token, expired.Token }));
        display.RemoveMissed(revoked);
        Check(display.MissedPrompts.Count == 1);
        display.ClearMissed();
        Check(display.MissedPrompts.Count == 0);
    }

    private static void MissedListDedupesAndBounds()
    {
        var now = Start;
        using var display = new PromptDisplayCoordinator(new Actions(), () => new View(), () => now);
        var old = Prompt(Start.AddMinutes(1), @"C:\apps\router.exe");
        display.Reconcile(new[] { old });
        display.Reconcile(Array.Empty<PromptWireDto>());
        Check(display.MissedPrompts.Count == 1);
        // The app tried again under the new policy: the live prompt replaces the entry.
        var fresh = Prompt(Start.AddMinutes(2), @"C:\APPS\Router.exe");
        int notifications = 0;
        display.MissedPromptsChanged += (_, e) => { notifications++; Check(e.Added.Count == 0); };
        display.Reconcile(new[] { fresh });
        Check(display.MissedPrompts.Count == 0 && notifications == 1);

        using var bounded = new PromptDisplayCoordinator(new Actions(), () => new View(), () => now);
        var many = Enumerable.Range(0, PromptDisplayCoordinator.MaximumMissedPrompts + 5)
            .Select(i => Prompt(Start.AddMinutes(2), $@"C:\apps\app{i}.exe")).ToArray();
        bounded.Reconcile(many);
        bounded.Reconcile(Array.Empty<PromptWireDto>());
        Check(bounded.MissedPrompts.Count == PromptDisplayCoordinator.MaximumMissedPrompts);
    }

    private static void FailedAllowIsMissed()
    {
        var actions = new Actions();
        actions.AllowResults.Enqueue(PromptActionStatus.Expired);
        var views = new List<View>();
        using var display = new PromptDisplayCoordinator(actions, () => NewView(views), () => Start);
        var prompt = Prompt(Start.AddMinutes(2));
        display.Reconcile(new[] { prompt });
        views[0].Allow();
        Check(views[0].Closed && display.MissedPrompts.Count == 1 && display.MissedPrompts[0].Token == prompt.Token);

        // A poll removed the popup while Allow was in flight and the Allow failed.
        var completion = new TaskCompletionSource<PromptActionStatus>();
        var pendingViews = new List<View>();
        using var pending = new PromptDisplayCoordinator(new Actions(), () => NewView(pendingViews), () => Start,
            action => completion.Task);
        var revoked = Prompt(Start.AddMinutes(2));
        pending.Reconcile(new[] { revoked });
        pendingViews[0].Allow();
        pending.Reconcile(Array.Empty<PromptWireDto>());
        Check(pending.MissedPrompts.Count == 0);
        completion.SetResult(PromptActionStatus.UnknownToken);
        Check(pending.MissedPrompts.Count == 1);
    }

    private static void AllowArmingNeedsDelayAndRiskProbe()
    {
        TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);
        var arming = new PromptAllowArming();
        arming.Reset();
        Check(!arming.IsArmed(At(10)));
        arming.NoteShownOrMoved(At(0));
        Check(!arming.IsArmed(At(5)));
        arming.NoteRiskReady();
        Check(!arming.IsArmed(At(0.9)));
        Check(arming.IsArmed(At(1)));
        arming.NoteShownOrMoved(At(1.5));
        Check(!arming.IsArmed(At(2.4)) && arming.IsArmed(At(2.5)));
        Check(!arming.IsArmed(At(1.4)));
        arming.Reset();
        Check(!arming.IsArmed(At(60)));
        arming.NoteRiskReady();
        Check(!arming.IsArmed(At(60)));
    }

    private static (PromptDisplayCoordinator Display, View View, PromptWireDto Prompt) LockedPrompt(
        Actions actions, Func<DateTimeOffset> now)
    {
        var view = new View();
        var display = new PromptDisplayCoordinator(actions, () => view, now);
        var prompt = Prompt(Start.AddMinutes(2));
        display.Reconcile(new[] { prompt });
        return (display, view, prompt);
    }

    private static void LockedAllowUnlocksAndRelocks()
    {
        var actions = new Actions { UnlockResult = PromptUnlockResult.Unlocked };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        var (display, view, prompt) = LockedPrompt(actions, () => Start);
        using (display)
        {
            view.Allow();
            Check(actions.UnlockCount == 1 && actions.AllowTokens.Count == 2);
            Check(actions.AllowTokens.All(token => token == prompt.Token));
            Check(actions.RelockCount == 1 && view.Closed && display.CurrentToken == null);
            Check(view.Failures.SequenceEqual(new[] { PromptActionStatus.Locked }) && actions.DismissCount == 0);
        }
    }

    private static void CancelledUnlockKeepsPromptOpen()
    {
        var actions = new Actions { UnlockResult = PromptUnlockResult.NotUnlocked };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        var (display, view, prompt) = LockedPrompt(actions, () => Start);
        using (display)
        {
            view.Allow();
            Check(actions.AllowCount == 1 && actions.RelockCount == 1 && actions.DismissCount == 0);
            Check(!view.Closed && display.CurrentToken == prompt.Token);
            Check(view.Failures.Count > 0 && view.Failures.All(status => status == PromptActionStatus.Locked));
            // The owner can try again from the same popup.
            actions.AllowResults.Enqueue(PromptActionStatus.Locked);
            actions.UnlockResult = PromptUnlockResult.Unlocked;
            view.Allow();
            Check(actions.UnlockCount == 2 && actions.AllowCount == 3 && actions.RelockCount == 2 && view.Closed);
        }
    }

    private static void LostUnlockResponseStillRelocks()
    {
        // The service may have applied UNLOCK even though the controller saw a failure.
        var actions = new Actions { DuringUnlock = () => throw new IOException("pipe lost") };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        var (display, view, _) = LockedPrompt(actions, () => Start);
        using (display)
        {
            view.Allow();
            Check(actions.UnlockCount == 1 && actions.AllowCount == 1 && actions.RelockCount == 1);
            Check(!view.Closed && view.Failures.All(status => status == PromptActionStatus.Locked));
        }
    }

    private static void AlreadyUnlockedRetriesWithoutRelock()
    {
        var actions = new Actions { UnlockResult = PromptUnlockResult.AlreadyUnlocked };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        var (display, view, _) = LockedPrompt(actions, () => Start);
        using (display)
        {
            view.Allow();
            Check(actions.AllowCount == 2 && actions.RelockCount == 0 && view.Closed);
        }
    }

    private static void UnlockPastExpiryNeverAllows()
    {
        var now = Start;
        var actions = new Actions { UnlockResult = PromptUnlockResult.Unlocked };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        actions.DuringUnlock = () => now = Start.AddMinutes(2);
        var (display, view, _) = LockedPrompt(actions, () => now);
        using (display)
        {
            view.Allow();
            Check(actions.AllowCount == 1 && actions.RelockCount == 1 && actions.DismissCount == 0);
            display.Tick();
            Check(view.Closed && display.CurrentToken == null && actions.DismissCount == 0);
        }
    }

    private static void WithdrawnDuringUnlockStillRelocks()
    {
        var actions = new Actions { UnlockResult = PromptUnlockResult.Unlocked };
        actions.AllowResults.Enqueue(PromptActionStatus.Locked);
        PromptDisplayCoordinator? current = null;
        actions.DuringUnlock = () => current!.Reconcile(Array.Empty<PromptWireDto>());
        var (display, view, _) = LockedPrompt(actions, () => Start);
        current = display;
        using (display)
        {
            view.Allow();
            Check(actions.AllowCount == 1 && actions.RelockCount == 1 && view.Closed && display.CurrentToken == null);
        }
    }

    private static void FailedTimeoutClosesOnce()
    {
        var now = Start;
        var views = new List<View>();
        var actions = new Actions { DismissResult = PromptActionStatus.ApplyFailed };
        using var display = new PromptDisplayCoordinator(actions, () => NewView(views), () => now);
        var prompt = Prompt(Start.AddMinutes(2));
        display.Reconcile(new[] { prompt });
        now = Start.AddSeconds(30);
        views[0].Timeout();
        Check(views[0].Closed && display.CurrentToken == null && actions.TimeoutCount == 1 && actions.DismissCount == 0);
        for (int index = 0; index < 20; index++)
        {
            views[0].Timeout();
            display.Reconcile(new[] { prompt });
            display.Tick();
        }
        Check(views.Count == 1 && actions.TimeoutCount == 1 && actions.DismissCount == 0 && display.CurrentToken == null);
    }

    private static void StalledTimeoutDoesNotDelayExpiry()
    {
        var now = Start;
        var views = new List<View>();
        var completion = new TaskCompletionSource<PromptActionStatus>();
        int scheduled = 0;
        using var display = new PromptDisplayCoordinator(new Actions(), () => NewView(views), () => now,
            action => { scheduled++; return completion.Task; });
        var first = Prompt(Start.AddMinutes(2));
        var next = Prompt(Start.AddSeconds(45));
        display.Reconcile(new[] { first, next });
        now = Start.AddSeconds(30);
        views[0].Timeout();
        Check(views[0].Closed && display.CurrentToken == next.Token && scheduled == 1 && !completion.Task.IsCompleted);
        now = Start.AddSeconds(45);
        display.Tick();
        Check(views[1].Closed && display.CurrentToken == null && !completion.Task.IsCompleted);
        completion.SetResult(PromptActionStatus.ApplyFailed);
        display.Reconcile(new[] { first, next });
        Check(display.CurrentToken == null && scheduled == 1);
    }

    private static void LateAllowCannotChangeReplacement()
    {
        var now = Start;
        var views = new List<View>();
        var completion = new TaskCompletionSource<PromptActionStatus>();
        using var display = new PromptDisplayCoordinator(new Actions(), () => NewView(views), () => now,
            action => completion.Task);
        var first = Prompt(Start.AddSeconds(1));
        var next = Prompt(Start.AddMinutes(2));
        display.Reconcile(new[] { first, next });
        views[0].Allow();
        now = Start.AddSeconds(1);
        display.Tick();
        Check(display.CurrentToken == next.Token && !views[1].Closed);
        completion.SetResult(PromptActionStatus.Allowed);
        Check(display.CurrentToken == next.Token && !views[1].Closed);
    }

    private static void AiRejectsUnsafeUrls()
    {
        foreach (string url in new[] { "http://example.com/v1", "http://127.0.0.1:8080/v1", "http://[::1]/v1",
            "file:///c:/secret", "https://user:password@example.com/v1", "https://example.com/v1?secret=value", "https://example.com/v1#fragment" })
            Check(!AiExplainSettings.TryBuildChatCompletionsUri(url, out _));
        Check(AiExplainSettings.TryBuildChatCompletionsUri("https://example.com/v1/", out var uri));
        Check(uri!.AbsoluteUri == "https://example.com/v1/chat/completions");
    }

    private static void AiPayloadDisclosure()
    {
        var prompt = Prompt(Start.AddMinutes(1));
        prompt.ExecutablePath = @"C:\Users\private-user\app.exe";
        prompt.SubjectKind = PromptIdentityKind.Service;
        prompt.ServiceName = "ExampleService";
        prompt.PackageSid = "S-1-15-2-example";
        prompt.RemoteAddress = "203.0.113.10";
        var subject = AiExplainSubject.FromPrompt(prompt, "Claimed Company", false);
        string text = AiExplainComposer.Compose(subject).UserPrompt;
        Check(text.Contains("app.exe") && text.Contains("ExampleService") && text.Contains("S-1-15-2-example"));
        Check(text.Contains("Claimed publisher (signature and trust unverified): \"Claimed Company\""));
        Check(!text.Contains("private-user") && !text.Contains("203.0.113.10"));
    }

    private static PromptWireDto Prompt(DateTimeOffset expires, string path = @"C:\apps\app.exe") => new()
    {
        Token = Guid.NewGuid(), CanAllow = true, ExpiresUtc = expires,
        SubjectKind = PromptIdentityKind.Executable, ExecutablePath = path,
    };
    private static View NewView(List<View> views) { var view = new View(); views.Add(view); return view; }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Hardening assertion failed."); }
    private sealed class Actions : IPromptActionClient
    {
        internal int AllowCount, AllowAlwaysCount, DismissCount, TimeoutCount, UnlockCount, RelockCount;
        internal readonly List<Guid> AllowTokens = new();
        public PromptActionStatus AllowAlways(Guid token)
        {
            AllowAlwaysCount++;
            AllowTokens.Add(token);
            return AllowResults.Count > 0 ? AllowResults.Dequeue() : PromptActionStatus.Allowed;
        }
        internal PromptActionStatus DismissResult = PromptActionStatus.Dismissed;
        internal Queue<PromptActionStatus> AllowResults = new();
        internal PromptUnlockResult UnlockResult = PromptUnlockResult.NotUnlocked;
        internal Action? DuringUnlock;
        public PromptActionStatus Allow(Guid token)
        {
            AllowCount++;
            AllowTokens.Add(token);
            return AllowResults.Count > 0 ? AllowResults.Dequeue() : PromptActionStatus.Allowed;
        }
        public PromptActionStatus Dismiss(Guid token) { DismissCount++; return DismissResult; }
        public PromptActionStatus DismissAfterTimeout(Guid token) { TimeoutCount++; return DismissResult; }
        public PromptUnlockResult Unlock() { UnlockCount++; DuringUnlock?.Invoke(); return UnlockResult; }
        public void Relock() => RelockCount++;
    }
    private sealed class View : IPromptView
    {
        public event EventHandler? AllowRequested;
        public event EventHandler? AlwaysAllowRequested;
        public event EventHandler? IgnoreRequested;
        public event EventHandler? PromptClosed { add { } remove { } }
        public event EventHandler? PromptTimedOut;
        internal bool Closed;
        internal int Waiting = -1;
        internal readonly List<PromptActionStatus> Failures = new();
        internal void Allow() => AllowRequested?.Invoke(this, EventArgs.Empty);
        internal void AlwaysAllow() => AlwaysAllowRequested?.Invoke(this, EventArgs.Empty);
        internal void Ignore() => IgnoreRequested?.Invoke(this, EventArgs.Empty);
        internal void Timeout() => PromptTimedOut?.Invoke(this, EventArgs.Empty);
        public void ShowPrompt(PromptWireDto prompt) { }
        public void ShowWaitingCount(int waiting) => Waiting = waiting;
        public void ShowActionFailure(PromptActionStatus status) => Failures.Add(status);
        public void ClosePrompt() => Closed = true;
        public void Dispose() { }
    }
}
