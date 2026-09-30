using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class ControllerPromptActionClient : IPromptActionClient
    {
        private readonly Controller _controller;
        private readonly Func<PromptUnlockResult> _unlock;
        private readonly Action _relock;

        internal ControllerPromptActionClient(Controller controller, Func<PromptUnlockResult> unlock, Action relock)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _unlock = unlock ?? throw new ArgumentNullException(nameof(unlock));
            _relock = relock ?? throw new ArgumentNullException(nameof(relock));
        }

        public PromptActionStatus Allow(Guid token) => _controller.AllowPrompt(token);
        public PromptActionStatus Dismiss(Guid token) => _controller.DismissPrompt(token);
        public PromptActionStatus DismissAfterTimeout(Guid token) => _controller.DismissPromptAfterTimeout(token);
        public PromptUnlockResult Unlock() => _unlock();
        public void Relock() => _relock();
    }
}
