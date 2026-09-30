using pylorak.TinyWall;
using pylorak.TinyWall.Prompting;

namespace SecureWall.Core.Tests;

// SW-13: the ten-minute password relock must not be refreshed by popup timeouts or by
// prompt actions on unknown or expired tokens.
internal static class ControllerActivityTests
{
    internal static IEnumerable<(string Name, Action Test)> Cases => new (string, Action)[]
    {
        ("popup timeout dismissal is never user activity", TimeoutDismissalIsNotActivity),
        ("prompt actions on unknown or expired tokens are not user activity", InvalidTokenActionsAreNotActivity),
        ("only successful explicit prompt actions are user activity", SuccessfulPromptActionsAreActivity),
        ("rejected and read requests are not user activity", RejectedAndReadRequestsAreNotActivity),
        ("random dismiss tokens and timeouts cannot hold the unlock window open", RandomDismissCannotHoldUnlockOpen),
        ("service classifies activity through the shared policy", ServiceUsesActivityPolicy),
    };

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    }

    private static bool Activity(MessageType request, PromptActionStatus status) =>
        ControllerActivityPolicy.IsUserActivity(request, request, status);

    private static void TimeoutDismissalIsNotActivity()
    {
        foreach (PromptActionStatus status in Enum.GetValues<PromptActionStatus>())
            AssertEx.False(Activity(MessageType.DISMISS_PROMPT_TIMEOUT, status), status.ToString());
    }

    private static void InvalidTokenActionsAreNotActivity()
    {
        foreach (var request in new[] { MessageType.DISMISS_PROMPT, MessageType.ALLOW_PROMPT })
        {
            AssertEx.False(Activity(request, PromptActionStatus.UnknownToken));
            AssertEx.False(Activity(request, PromptActionStatus.Expired));
        }
        AssertEx.False(Activity(MessageType.ALLOW_PROMPT, PromptActionStatus.NotAllowable));
        AssertEx.False(Activity(MessageType.ALLOW_PROMPT, PromptActionStatus.ApplyFailed));
        AssertEx.False(ControllerActivityPolicy.IsUserActivity(MessageType.DISMISS_PROMPT, MessageType.DISMISS_PROMPT, null));
    }

    private static void SuccessfulPromptActionsAreActivity()
    {
        AssertEx.True(Activity(MessageType.DISMISS_PROMPT, PromptActionStatus.Dismissed));
        AssertEx.True(Activity(MessageType.ALLOW_PROMPT, PromptActionStatus.Allowed));
        // A status only counts for the action that produces it.
        AssertEx.False(Activity(MessageType.DISMISS_PROMPT, PromptActionStatus.Allowed));
        AssertEx.False(Activity(MessageType.ALLOW_PROMPT, PromptActionStatus.Dismissed));
    }

    private static void RejectedAndReadRequestsAreNotActivity()
    {
        foreach (var request in new[] { MessageType.UNLOCK, MessageType.MODE_SWITCH, MessageType.PUT_SETTINGS, MessageType.SET_PASSPHRASE })
        {
            AssertEx.True(ControllerActivityPolicy.IsUserActivity(request, request, null), request.ToString());
            foreach (var rejected in new[] { MessageType.RESPONSE_ERROR, MessageType.RESPONSE_LOCKED, MessageType.COM_ERROR })
                AssertEx.False(ControllerActivityPolicy.IsUserActivity(request, rejected, null), request + "/" + rejected);
        }
        AssertEx.False(ControllerActivityPolicy.IsUserActivity(MessageType.ALLOW_PROMPT, MessageType.RESPONSE_LOCKED, null));
        foreach (var read in new[] { MessageType.GET_SETTINGS, MessageType.READ_PENDING_PROMPTS, MessageType.READ_FW_LOG, MessageType.IS_LOCKED, MessageType.GET_PROCESS_PATH })
            AssertEx.False(ControllerActivityPolicy.IsUserActivity(read, read, null), read.ToString());
    }

    // Same sequence as PipeServerDataReceived: queue action, then Record(IsUserActivity(...)).
    private static void RandomDismissCannotHoldUnlockOpen()
    {
        var clock = new Clock();
        var queue = new PromptQueue(clock);
        var activity = new UserActivityTimeout(clock, TimeSpan.FromMinutes(10));
        void Send(MessageType type, Func<Guid, PromptActionResult> action, Guid token) =>
            activity.Record(ControllerActivityPolicy.IsUserActivity(type, type, action(token).Status));

        for (int minute = 0; minute < 11; minute++)
        {
            clock.UtcNow += TimeSpan.FromMinutes(1);
            Send(MessageType.DISMISS_PROMPT, queue.Dismiss, Guid.NewGuid());
            Send(MessageType.ALLOW_PROMPT, queue.Allow, Guid.NewGuid());
            Guid timedOut = queue.Enqueue(PromptIdentity.ForExecutable(@"C:\apps\retry" + minute + ".exe"), "203.0.113.1", 443, 6).Token;
            Send(MessageType.DISMISS_PROMPT_TIMEOUT, queue.Dismiss, timedOut);
        }
        AssertEx.True(activity.Expired, "Timeouts and invalid tokens must let the unlock window lapse.");

        var fresh = new UserActivityTimeout(clock, TimeSpan.FromMinutes(10));
        clock.UtcNow += TimeSpan.FromMinutes(9);
        Guid explicitToken = queue.Enqueue(PromptIdentity.ForExecutable(@"C:\apps\owner.exe"), "203.0.113.2", 443, 6).Token;
        fresh.Record(ControllerActivityPolicy.IsUserActivity(MessageType.DISMISS_PROMPT, MessageType.DISMISS_PROMPT,
            queue.Dismiss(explicitToken).Status));
        clock.UtcNow += TimeSpan.FromMinutes(9);
        AssertEx.False(fresh.Expired, "An explicit dismissal of a pending prompt is owner activity.");
    }

    private static void ServiceUsesActivityPolicy()
    {
        string service = PromptTransactionIntegrationTests.Source("TinyWall/TinyWallService.cs");
        int start = service.IndexOf("private TwMessage PipeServerDataReceived(TwMessage reqMsg)", StringComparison.Ordinal);
        int end = service.IndexOf("public void RequestStop()", start, StringComparison.Ordinal);
        AssertEx.True(start >= 0 && end > start);
        string body = service.Substring(start, end - start);
        AssertEx.True(body.Contains("ControllerActivity.Record(ControllerActivityPolicy.IsUserActivity(", StringComparison.Ordinal));
        AssertEx.Equal(1, CountOf(body, "ControllerActivity.Record("));
        AssertEx.Equal(1, CountOf(service, "ControllerActivity.Record("));
        AssertEx.True(service.Contains("case MessageType.DISMISS_PROMPT:\n                case MessageType.DISMISS_PROMPT_TIMEOUT:", StringComparison.Ordinal));

        string coordinator = PromptTransactionIntegrationTests.Source("TinyWall/Prompting/PromptDisplayCoordinator.cs");
        int timeout = coordinator.IndexOf("private async void PromptTimedOut(", StringComparison.Ordinal);
        int ignore = coordinator.IndexOf("private async void IgnoreCurrent()", timeout, StringComparison.Ordinal);
        string timeoutBody = coordinator.Substring(timeout, ignore - timeout);
        AssertEx.True(timeoutBody.Contains("_actions.DismissAfterTimeout(token)", StringComparison.Ordinal));
        AssertEx.False(timeoutBody.Contains("_actions.Dismiss(token)", StringComparison.Ordinal));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
