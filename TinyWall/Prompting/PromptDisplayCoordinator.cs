using System;
using System.Collections.Generic;

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
        private readonly IPromptActionClient _actions;
        private readonly Func<IPromptView> _viewFactory;
        private readonly Queue<PromptWireDto> _pending = new Queue<PromptWireDto>();
        private readonly HashSet<Guid> _knownTokens = new HashSet<Guid>();
        private IPromptView? _currentView;
        private PromptWireDto? _current;
        private bool _disposed;

        internal PromptDisplayCoordinator(
            IPromptActionClient actions,
            Func<IPromptView> viewFactory)
        {
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _viewFactory = viewFactory ?? throw new ArgumentNullException(nameof(viewFactory));
        }

        internal Guid? CurrentToken => _current?.Token;
        internal int PendingCount => _pending.Count;

        internal void Enqueue(IEnumerable<PromptWireDto> prompts)
        {
            if (prompts == null)
                throw new ArgumentNullException(nameof(prompts));
            ThrowIfDisposed();

            foreach (PromptWireDto prompt in prompts)
            {
                if (prompt == null || prompt.Token == Guid.Empty || !_knownTokens.Add(prompt.Token))
                    continue;
                _pending.Enqueue(prompt);
            }

            ShowNextIfIdle();
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
        }

        private void ShowNextIfIdle()
        {
            if (_disposed || _current != null || _pending.Count == 0)
                return;

            _current = _pending.Dequeue();
            _currentView = _viewFactory();
            _currentView.AllowRequested += AllowRequested;
            _currentView.IgnoreRequested += IgnoreRequested;
            _currentView.PromptClosed += PromptClosed;
            _currentView.PromptTimedOut += PromptTimedOut;
            _currentView.ShowPrompt(_current);
        }

        private void AllowRequested(object? sender, EventArgs eventArgs)
        {
            if (_current == null || _currentView == null)
                return;

            if (!_current.CanAllow)
            {
                _currentView.ShowActionFailure(PromptActionStatus.NotAllowable);
                return;
            }

            PromptActionStatus status = _actions.Allow(_current.Token);
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
        private void PromptTimedOut(object? sender, EventArgs eventArgs) => IgnoreCurrent();

        private void IgnoreCurrent()
        {
            if (_current == null || _currentView == null)
                return;

            PromptActionStatus status = _actions.Dismiss(_current.Token);
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
