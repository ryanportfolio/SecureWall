using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace pylorak.TinyWall.Prompting
{
    internal enum PromptUnlockResult
    {
        NotUnlocked,
        AlreadyUnlocked,
        Unlocked,
    }

    internal interface IPromptActionClient
    {
        PromptActionStatus Allow(Guid token);
        // Allow, plus an Always allow rule for the file name and the signer the service verifies.
        PromptActionStatus AllowAlways(Guid token);
        // Closing the popup: no prompt for this identity for five minutes.
        PromptActionStatus Dismiss(Guid token);
        // The Ignore button: Dismiss, and the service also adds the identity to the Ignored list
        // while it is unlocked.
        PromptActionStatus Ignore(Guid token);
        // Same effect as Dismiss, but marked as automatic so it never extends the unlock window.
        PromptActionStatus DismissAfterTimeout(Guid token);

        // Runs on the UI thread: shows the same password dialog as the tray. Unlocked means
        // this call performed the unlock; AlreadyUnlocked means no password was needed.
        PromptUnlockResult Unlock();

        // Restores the lock after the popup asked for the password (the Allow round trip
        // ends, or the unlock was cancelled, failed, or its response was lost).
        void Relock();
    }

    internal interface IPromptView : IDisposable
    {
        event EventHandler? AllowRequested;
        event EventHandler? AlwaysAllowRequested;
        event EventHandler? IgnoreRequested;
        event EventHandler? PromptClosed;
        event EventHandler? PromptTimedOut;

        void ShowPrompt(PromptWireDto prompt);
        // How many other prompts are queued behind this one.
        void ShowWaitingCount(int waiting);
        void ShowActionFailure(PromptActionStatus status);
        void ClosePrompt();
    }

    internal sealed class MissedPromptsChangedEventArgs : EventArgs
    {
        internal MissedPromptsChangedEventArgs(IReadOnlyList<PromptWireDto> added) => Added = added;

        // Prompts that went unanswered since the previous notification. May be empty when
        // entries were only removed.
        internal IReadOnlyList<PromptWireDto> Added { get; }
    }

    internal sealed class PromptDisplayCoordinator : IDisposable
    {
        internal const int MaximumDisplayedPrompts = 64;
        internal const int MaximumMissedPrompts = 20;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly IPromptActionClient _actions;
        private readonly Func<IPromptView> _viewFactory;
        private readonly Func<Func<PromptActionStatus>, Task<PromptActionStatus>> _performAction;
        private readonly Queue<PromptWireDto> _pending = new Queue<PromptWireDto>();
        private readonly HashSet<Guid> _knownTokens = new HashSet<Guid>();
        private readonly Dictionary<Guid, DateTimeOffset> _locallyClosed = new Dictionary<Guid, DateTimeOffset>();
        private readonly HashSet<Guid> _actionsInFlight = new HashSet<Guid>();
        // Display-only record of prompts that left without an answer from the owner (expired,
        // or revoked by a policy change). Newest first, one entry per identity. Entries carry
        // no authority: their tokens are dead, and acting on one goes through the owner's
        // normal exception path.
        private readonly List<PromptWireDto> _missed = new List<PromptWireDto>();
        private readonly List<PromptWireDto> _missedAdded = new List<PromptWireDto>();
        private bool _missedChanged;
        private IPromptView? _currentView;
        private PromptWireDto? _current;
        private bool _disposed;

        internal event EventHandler<MissedPromptsChangedEventArgs>? MissedPromptsChanged;
        internal IReadOnlyList<PromptWireDto> MissedPrompts => _missed.AsReadOnly();

        internal PromptDisplayCoordinator(
            IPromptActionClient actions,
            Func<IPromptView> viewFactory,
            Func<DateTimeOffset>? utcNow = null,
            Func<Func<PromptActionStatus>, Task<PromptActionStatus>>? performAction = null)
        {
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _viewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
            _performAction = performAction ?? (action => Task.FromResult(action()));
        }

        internal Guid? CurrentToken => _current?.Token;
        internal int PendingCount => _pending.Count;

        internal void Enqueue(IEnumerable<PromptWireDto> prompts)
        {
            if (prompts == null)
                throw new ArgumentNullException(nameof(prompts));
            ThrowIfDisposed();

            RemoveExpired();
            foreach (PromptWireDto prompt in prompts)
            {
                if (prompt == null || prompt.Token == Guid.Empty || prompt.ExpiresUtc <= _utcNow() ||
                    _locallyClosed.ContainsKey(prompt.Token) ||
                    _knownTokens.Count >= MaximumDisplayedPrompts || !_knownTokens.Add(prompt.Token))
                    continue;
                _pending.Enqueue(prompt);
                // A live prompt for the same identity replaces its missed entry.
                _missedAdded.RemoveAll(m => SameIdentity(m, prompt));
                if (_missed.RemoveAll(m => SameIdentity(m, prompt)) > 0)
                    _missedChanged = true;
            }

            ShowNextIfIdle();
        }

        // A poll is a complete service snapshot, not an append-only feed.
        internal void Reconcile(IEnumerable<PromptWireDto> prompts)
        {
            if (prompts == null) throw new ArgumentNullException(nameof(prompts));
            ThrowIfDisposed();
            var snapshot = new List<PromptWireDto>();
            var tokens = new HashSet<Guid>();
            DateTimeOffset now = _utcNow();
            foreach (PromptWireDto prompt in prompts)
            {
                if (prompt == null || prompt.Token == Guid.Empty || prompt.ExpiresUtc <= now ||
                    !tokens.Add(prompt.Token)) continue;
                snapshot.Add(prompt);
                if (snapshot.Count == MaximumDisplayedPrompts) break;
            }
            // The service dropped these without an answer from here: they expired, or a
            // policy change revoked their tokens.
            foreach (PromptWireDto prompt in _pending)
                if (!tokens.Contains(prompt.Token)) RecordMissed(prompt);
            _pending.Clear();
            _knownTokens.Clear();
            if (_current != null && (_current.ExpiresUtc <= now || !tokens.Contains(_current.Token)))
            {
                RecordMissed(_current);
                CloseCurrentView();
                _current = null;
            }
            if (_current != null) _knownTokens.Add(_current.Token);
            Enqueue(snapshot);
        }

        // Runs before starting asynchronous polling, including when an earlier read is stalled.
        internal void Tick()
        {
            if (_disposed) return;
            RemoveExpired();
            ShowNextIfIdle();
        }

        private void RemoveExpired()
        {
            DateTimeOffset now = _utcNow();
            foreach (Guid token in new List<Guid>(_locallyClosed.Keys))
                if (_locallyClosed[token] <= now) _locallyClosed.Remove(token);
            if (_current != null && _current.ExpiresUtc <= now)
            {
                RecordMissed(_current);
                _knownTokens.Remove(_current.Token);
                CloseCurrentView();
                _current = null;
            }
            int count = _pending.Count;
            for (int index = 0; index < count; index++)
            {
                PromptWireDto prompt = _pending.Dequeue();
                if (prompt.ExpiresUtc <= now)
                {
                    RecordMissed(prompt);
                    _knownTokens.Remove(prompt.Token);
                }
                else _pending.Enqueue(prompt);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            CloseCurrentView();
            _current = null;
            _pending.Clear();
            _knownTokens.Clear();
            _locallyClosed.Clear();
            _missed.Clear();
            _missedAdded.Clear();
        }

        // The owner acted on a missed entry, or cleared the list.
        internal void RemoveMissed(PromptWireDto prompt)
        {
            if (_disposed || prompt == null) return;
            if (_missed.RemoveAll(m => SameIdentity(m, prompt)) > 0)
                _missedChanged = true;
            NotifyMissed();
        }

        internal void ClearMissed()
        {
            if (_disposed || _missed.Count == 0) return;
            _missed.Clear();
            _missedChanged = true;
            NotifyMissed();
        }

        private void ShowNextIfIdle()
        {
            if (_disposed) return;
            RemoveExpired();
            if (_current == null && _pending.Count > 0)
            {
                _current = _pending.Dequeue();
                _currentView = _viewFactory();
                _currentView.AllowRequested += AllowRequested;
                _currentView.AlwaysAllowRequested += AlwaysAllowRequested;
                _currentView.IgnoreRequested += IgnoreRequested;
                _currentView.PromptClosed += PromptClosed;
                _currentView.PromptTimedOut += PromptTimedOut;
                _currentView.ShowPrompt(_current);
            }
            _currentView?.ShowWaitingCount(_pending.Count);
            NotifyMissed();
        }

        // An action already sent for this token decides its fate; the owner answered it.
        private void RecordMissed(PromptWireDto prompt)
        {
            if (_disposed || _actionsInFlight.Contains(prompt.Token)) return;
            _missed.RemoveAll(m => SameIdentity(m, prompt));
            _missed.Insert(0, prompt);
            if (_missed.Count > MaximumMissedPrompts)
                _missed.RemoveAt(_missed.Count - 1);
            _missedAdded.RemoveAll(m => SameIdentity(m, prompt));
            _missedAdded.Add(prompt);
            _missedChanged = true;
        }

        private void NotifyMissed()
        {
            if (_disposed || !_missedChanged) return;
            var added = _missedAdded.ToArray();
            _missedAdded.Clear();
            _missedChanged = false;
            MissedPromptsChanged?.Invoke(this, new MissedPromptsChangedEventArgs(added));
        }

        internal static bool SameIdentity(PromptWireDto first, PromptWireDto second) =>
            first.SubjectKind == second.SubjectKind &&
            first.AllowBlocker == second.AllowBlocker &&
            string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(first.ServiceName, second.ServiceName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(first.PackageSid, second.PackageSid, StringComparison.OrdinalIgnoreCase);

        private void AllowRequested(object? sender, EventArgs eventArgs) => RequestAllow(alwaysAllow: false);
        private void AlwaysAllowRequested(object? sender, EventArgs eventArgs) => RequestAllow(alwaysAllow: true);

        private async void RequestAllow(bool alwaysAllow)
        {
            if (_current == null || _currentView == null)
                return;

            if (_current.ExpiresUtc <= _utcNow())
            {
                // The owner wanted to allow it: keep it reachable from the missed list.
                RecordMissed(_current);
                CompleteCurrent();
                return;
            }
            if (!_current.CanAllow)
            {
                _currentView.ShowActionFailure(PromptActionStatus.NotAllowable);
                return;
            }

            PromptWireDto prompt = _current;
            Guid token = prompt.Token;
            IPromptView view = _currentView;
            if (!_actionsInFlight.Add(token)) return;
            PromptActionStatus status = await PerformAction(() => alwaysAllow ? _actions.AllowAlways(token) : _actions.Allow(token));
            bool relock = false;
            if (status == PromptActionStatus.Locked && IsCurrent(token, view))
            {
                // The popup stays open until the token expires, so the owner can unlock here and
                // this same token is retried. The service is unlocked globally for this one
                // Allow round trip. Relock whenever the password was asked for: a cancelled or
                // failed unlock may still have been applied if its response was lost, and LOCK
                // on a locked service is a no-op.
                view.ShowActionFailure(PromptActionStatus.Locked);
                PromptUnlockResult unlock = TryUnlock();
                relock = unlock != PromptUnlockResult.AlreadyUnlocked;
                if (unlock != PromptUnlockResult.NotUnlocked && IsCurrent(token, view) &&
                    _current!.ExpiresUtc > _utcNow())
                {
                    status = await PerformAction(() => alwaysAllow ? _actions.AllowAlways(token) : _actions.Allow(token));
                }
            }
            if (relock)
                TryRelock();
            _actionsInFlight.Remove(token);
            if (!IsCurrent(token, view))
            {
                // A poll closed the popup while Allow was in flight. Unless it was granted,
                // the owner's Allow did not happen; keep the prompt reachable.
                if (status != PromptActionStatus.Allowed)
                {
                    RecordMissed(prompt);
                    NotifyMissed();
                }
                return;
            }
            if (status == PromptActionStatus.Allowed ||
                status == PromptActionStatus.Expired ||
                status == PromptActionStatus.UnknownToken)
            {
                if (status != PromptActionStatus.Allowed)
                    RecordMissed(prompt);
                CompleteCurrent();
            }
            else
            {
                _currentView.ShowActionFailure(status);
            }
        }

        private void IgnoreRequested(object? sender, EventArgs eventArgs) => IgnoreCurrent(remember: true);
        private void PromptClosed(object? sender, EventArgs eventArgs) => IgnoreCurrent(remember: false);
        private async void PromptTimedOut(object? sender, EventArgs eventArgs)
        {
            if (_current == null || _currentView == null) return;
            Guid token = _current.Token;
            bool stillValid = _current.ExpiresUtc > _utcNow();
            // A lost dismissal must not reopen this same prompt on the next poll.
            if (stillValid)
            {
                if (_locallyClosed.Count >= MaximumDisplayedPrompts)
                {
                    Guid oldest = Guid.Empty;
                    DateTimeOffset expiry = DateTimeOffset.MaxValue;
                    foreach (var item in _locallyClosed)
                        if (item.Value < expiry) { oldest = item.Key; expiry = item.Value; }
                    _locallyClosed.Remove(oldest);
                }
                _locallyClosed[token] = _current.ExpiresUtc;
            }
            RecordMissed(_current);
            CompleteCurrent();
            if (!stillValid || !_actionsInFlight.Add(token)) return;
            // Local closure happens first. One best-effort dismissal never delays
            // expiry, retries on the UI timer, or applies a result to another view.
            await PerformAction(() => _actions.DismissAfterTimeout(token));
            _actionsInFlight.Remove(token);
        }

        private async void IgnoreCurrent(bool remember)
        {
            if (_current == null || _currentView == null)
                return;

            if (_current.ExpiresUtc <= _utcNow())
            {
                CompleteCurrent();
                return;
            }
            Guid token = _current.Token;
            IPromptView view = _currentView;
            if (!_actionsInFlight.Add(token)) return;
            PromptActionStatus status = await PerformAction(() => remember ? _actions.Ignore(token) : _actions.Dismiss(token));
            if (remember && status == PromptActionStatus.Locked)
            {
                // Saving to the Ignored list needs the password, so Ignore unlocks, retries and
                // relocks the way Allow does. A cancelled or failed unlock falls back to a plain
                // dismissal: the app stays blocked and is asked about again after five minutes.
                bool relock = false;
                if (IsCurrent(token, view) && _current!.ExpiresUtc > _utcNow())
                {
                    PromptUnlockResult unlock = TryUnlock();
                    relock = unlock != PromptUnlockResult.AlreadyUnlocked;
                    if (unlock != PromptUnlockResult.NotUnlocked && IsCurrent(token, view) &&
                        _current!.ExpiresUtc > _utcNow())
                    {
                        status = await PerformAction(() => _actions.Ignore(token));
                    }
                }
                if (status == PromptActionStatus.Locked && IsCurrent(token, view) && _current!.ExpiresUtc > _utcNow())
                    status = await PerformAction(() => _actions.Dismiss(token));
                if (relock)
                    TryRelock();
            }
            _actionsInFlight.Remove(token);
            if (_disposed || _current?.Token != token || !ReferenceEquals(view, _currentView)) return;
            if (status == PromptActionStatus.Dismissed ||
                status == PromptActionStatus.Expired ||
                status == PromptActionStatus.UnknownToken)
            {
                CompleteCurrent();
            }
            else
            {
                _currentView.ShowActionFailure(status);
            }
        }

        private async Task<PromptActionStatus> PerformAction(Func<PromptActionStatus> action)
        {
            try { return await _performAction(action); }
            catch { return PromptActionStatus.ApplyFailed; }
        }

        private bool IsCurrent(Guid token, IPromptView view) =>
            !_disposed && _current?.Token == token && ReferenceEquals(view, _currentView);

        private PromptUnlockResult TryUnlock()
        {
            try { return _actions.Unlock(); }
            catch { return PromptUnlockResult.NotUnlocked; }
        }

        // Best effort: if the lock request fails, the service's inactivity relock still applies.
        private void TryRelock()
        {
            try { _actions.Relock(); }
            catch { }
        }

        private void CompleteCurrent()
        {
            if (_current != null)
                _knownTokens.Remove(_current.Token);
            CloseCurrentView();
            _current = null;
            ShowNextIfIdle();
        }

        private void CloseCurrentView()
        {
            if (_currentView == null)
                return;

            _currentView.AllowRequested -= AllowRequested;
            _currentView.AlwaysAllowRequested -= AlwaysAllowRequested;
            _currentView.IgnoreRequested -= IgnoreRequested;
            _currentView.PromptClosed -= PromptClosed;
            _currentView.PromptTimedOut -= PromptTimedOut;
            _currentView.ClosePrompt();
            _currentView.Dispose();
            _currentView = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PromptDisplayCoordinator));
        }
    }
}
