using System;

namespace pylorak.TinyWall.Prompting
{
    internal sealed class ControllerPromptActionClient : IPromptActionClient
    {
        private readonly Controller _controller;

        internal ControllerPromptActionClient(Controller controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        public PromptActionStatus Allow(Guid token) => _controller.AllowPrompt(token);
        public PromptActionStatus Dismiss(Guid token) => _controller.DismissPrompt(token);
    }
}
