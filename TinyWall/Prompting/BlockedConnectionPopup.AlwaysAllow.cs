using System;
using System.IO;
using System.Threading.Tasks;

namespace pylorak.TinyWall.Prompting
{
    // The Always allow button: shown for executable prompts, enabled once the file's embedded
    // signature checks out (off the UI thread) and Allow itself is armed. The check here only
    // decides what the button offers; the service verifies the file again before saving.
    internal sealed partial class BlockedConnectionPopup
    {
        private string? _alwaysAllowSigner;
        private Guid _signerToken;

        // Replaceable for the /promptpreview path. Defaults to the real check.
        internal Func<string?, SignedFileIdentity?> SignerProbe { get; set; } = SignedFileIdentity.TryRead;

        public event EventHandler? AlwaysAllowRequested;

        // Called from ShowPrompt for each new prompt.
        private void SetAlwaysAllowPrompt(PromptWireDto prompt)
        {
            _alwaysAllowSigner = null;
            _signerToken = prompt.Token;
            bool eligible = prompt.SubjectKind == PromptIdentityKind.Executable && prompt.CanAllow &&
                !string.IsNullOrWhiteSpace(prompt.ExecutablePath);
            alwaysAllowButton.Visible = eligible;
            toolTip.SetToolTip(alwaysAllowButton, "Checking the file's signature.");
            if (!eligible)
                return;

            string path = prompt.ExecutablePath!;
            Guid token = prompt.Token;
            Func<string?, SignedFileIdentity?> probe = SignerProbe;
            Task.Run(() =>
            {
                try
                {
                    return probe(path);
                }
                catch (Exception)
                {
                    return null;
                }
            }).ContinueWith(task =>
            {
                if (IsDisposed || !IsHandleCreated || token != _signerToken)
                    return;
                SignedFileIdentity? signer = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
                _alwaysAllowSigner = signer?.Name;
                toolTip.SetToolTip(alwaysAllowButton, signer == null
                    ? "Always allow needs a valid embedded signature, so SecureWall can recognize this app after it moves. Use Allow instead."
                    : $"Allow now, and from now on allow any file named {Path.GetFileName(path)} signed by {signer.Name}, in any folder. Outbound TCP/UDP only.");
                UpdateAllowButton();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void AlwaysAllowButtonClick(object? sender, EventArgs eventArgs)
        {
            UpdateAllowButton();
            if (!alwaysAllowButton.Enabled)
                return;
            _allowInFlight = true;
            allowButton.Enabled = false;
            alwaysAllowButton.Enabled = false;
            AlwaysAllowRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
