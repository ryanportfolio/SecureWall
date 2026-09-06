using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace pylorak.TinyWall.Prompting
{
    internal interface IPromptActionClient
    {
        PromptActionStatus Allow(Guid token);
        PromptActionStatus Dismiss(Guid token);
    }

    internal interface IPromptView : IDisposable
    {
        event EventHandler? AllowRequested;
        event EventHandler? IgnoreRequested;
        event EventHandler? PromptClosed;
        event EventHandler? PromptTimedOut;

        void ShowPrompt(PromptWireDto prompt);
        void ShowActionFailure(PromptActionStatus status);
        void ClosePrompt();
    }

    internal sealed class PromptDisplayCoordinator : IDisposable
    {
        internal const int MaximumDisplayedPrompts = 64;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly IPromptActionClient _actions;
        private readonly Func<IPromptView> _viewFactory;
        private readonly Func<Func<PromptActionStatus>, Task<PromptActionStatus>> _performAction;
        private readonly Queue<PromptWireDto> _pending = new Queue<PromptWireDto>();
        private readonly HashSet<Guid> _knownTokens = new HashSet<Guid>();
        private readonly Dictionary<Guid, DateTimeOffset> _locallyClosed = new Dictionary<Guid, DateTimeOffset>();
        private readonly HashSet<Guid> _actionsInFlight = new HashSet<Guid>();
        private IPromptView? _currentView;
        private PromptWireDto? _current;
        private bool _disposed;

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
            _pending.Clear();
            _knownTokens.Clear();
            if (_current != null && (_current.ExpiresUtc <= now || !tokens.Contains(_current.Token)))
            {
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
                _knownTokens.Remove(_current.Token);
                CloseCurrentView();
                _current = null;
            }
            int count = _pending.Count;
            for (int index = 0; index < count; index++)
            {
                PromptWireDto prompt = _pending.Dequeue();
                if (prompt.ExpiresUtc <= now) _knownTokens.Remove(prompt.Token);
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
        }

        private void ShowNextIfIdle()
        {
            if (_disposed) return;
            RemoveExpired();
            if (_current != null || _pending.Count == 0)
                return;

            _current = _pending.Dequeue();
            _currentView = _viewFactory();
            _currentView.AllowRequested += AllowRequested;
            _currentView.IgnoreRequested += IgnoreRequested;
            _currentView.PromptClosed += PromptClosed;
            _currentView.PromptTimedOut += PromptTimedOut;
            _currentView.ShowPrompt(_current);
        }

        private async void AllowRequested(object? sender, EventArgs eventArgs)
        {
            if (_current == null || _currentView == null)
                return;

            if (_current.ExpiresUtc <= _utcNow())
            {
                CompleteCurrent();
                return;
            }
            if (!_current.CanAllow)
            {
                _currentView.ShowActionFailure(PromptActionStatus.NotAllowable);
                return;
            }

            Guid token = _current.Token;
            IPromptView view = _currentView;
            if (!_actionsInFlight.Add(token)) return;
            PromptActionStatus status = await PerformAction(() => _actions.Allow(token));
            _actionsInFlight.Remove(token);
            if (_disposed || _current?.Token != token || !ReferenceEquals(view, _currentView)) return;
            if (status == PromptActionStatus.Allowed ||
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

        private void IgnoreRequested(object? sender, EventArgs eventArgs) => IgnoreCurrent();
        private void PromptClosed(object? sender, EventArgs eventArgs) => IgnoreCurrent();
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
            CompleteCurrent();
            if (!stillValid || !_actionsInFlight.Add(token)) return;
            // Local closure happens first. One best-effort dismissal never delays
            // expiry, retries on the UI timer, or applies a result to another view.
            await PerformAction(() => _actions.Dismiss(token));
            _actionsInFlight.Remove(token);
        }

        private async void IgnoreCurrent()
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
            PromptActionStatus status = await PerformAction(() => _actions.Dismiss(token));
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
