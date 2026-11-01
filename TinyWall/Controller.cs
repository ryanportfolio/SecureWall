using System;
using System.Collections.Generic;
using pylorak.Utilities;
using pylorak.TinyWall.Prompting;

namespace pylorak.TinyWall
{
    public sealed class Controller
    {
        private readonly PipeClientEndpoint Endpoint;

        public Controller(string serverEndpoint)
        {
            Endpoint = new PipeClientEndpoint(serverEndpoint);
        }

#if DEBUG
        internal Controller(string serverEndpoint, int expectedTestServerProcessId)
        {
            Endpoint = new PipeClientEndpoint(serverEndpoint, expectedTestServerProcessId);
        }
#endif

        public MessageType GetServerConfig(out ServerConfiguration? serverConfig, out ServerState? serverState, ref Guid clientChangeset)
        {
            // Detect if server settings have changed in comparison to ours and download
            // settings only if we need them. Settings are "version numbered" using the "changeset"
            // property. We send our changeset number to the service and if it differs from his,
            // the service will send back the settings.

            serverConfig = null;
            serverState = null;

            var resp = Endpoint.QueueMessage(TwMessageGetSettings.CreateRequest(clientChangeset)).Response;
            if (resp.Type == MessageType.GET_SETTINGS)
            {
                var respArgs = (TwMessageGetSettings)resp;
                if (respArgs.Changeset != clientChangeset)
                {
                    clientChangeset = respArgs.Changeset;
                    serverConfig = respArgs.Config;
                    serverState = respArgs.State;
                }
            }

            return resp.Type;
        }

        public TwMessage SetServerConfig(ServerConfiguration serverConfig, Guid clientChangeset)
        {
            return Endpoint.QueueMessage(TwMessagePutSettings.CreateRequest(clientChangeset, serverConfig)).Response;
        }

        public TwRequest BeginReadFwLog()
        {
            return Endpoint.QueueMessage(TwMessageReadFwLog.CreateRequest());
        }

        public static FirewallLogEntry[] EndReadFwLog(TwMessage twResp)
        {
            if (twResp is TwMessageReadFwLog fwLog)
                return fwLog.Entries;
            else
                // TODO: Do we want to show an error to the user?
                return Array.Empty<FirewallLogEntry>();
        }

        internal TwRequest BeginReadPendingPrompts()
        {
            return Endpoint.QueueMessage(TwMessageReadPendingPrompts.CreateRequest());
        }

        internal static PromptWireDto[] EndReadPendingPrompts(TwMessage response)
        {
            return response is TwMessageReadPendingPrompts prompts
                ? prompts.Prompts
                : Array.Empty<PromptWireDto>();
        }

        internal PromptActionStatus DismissPrompt(Guid token) =>
            SendDismiss(TwMessagePromptAction.CreateDismissRequest(token));

        // The popup's Ignore button: dismisses and adds the app to the Ignored list. Locked when there
        // is an entry to save and the service is locked; the prompt then stays pending.
        internal PromptActionStatus IgnorePrompt(Guid token) =>
            SendDismiss(TwMessagePromptAction.CreateIgnoreRequest(token));

        // Automatic popup timeout: the service must not count it as user activity.
        internal PromptActionStatus DismissPromptAfterTimeout(Guid token) =>
            SendDismiss(TwMessagePromptAction.CreateTimeoutDismissRequest(token));

        private PromptActionStatus SendDismiss(TwMessagePromptAction request)
        {
            TwMessage response = Endpoint.QueueMessage(request).Response;
            return response is TwMessagePromptAction action
                ? action.Status
                : PromptActionStatus.ApplyFailed;
        }

        internal PromptActionStatus AllowPrompt(Guid token) =>
            SendAllow(TwMessagePromptAction.CreateAllowRequest(token));

        internal PromptActionStatus AllowPromptAlways(Guid token) =>
            SendAllow(TwMessagePromptAction.CreateAllowAlwaysRequest(token));

        private PromptActionStatus SendAllow(TwMessagePromptAction request)
        {
            TwMessage response = Endpoint.QueueMessage(request).Response;
            if (response is TwMessagePromptAction action)
                return action.Status;
            if (response is TwMessageLocked)
                return PromptActionStatus.Locked;
            return PromptActionStatus.ApplyFailed;
        }

        // Check for updates. Locked comes back as RESPONSE_LOCKED with status Failed.
        internal MessageType RequestUpdateNetwork(out UpdateGrantStatus status)
        {
            TwMessage response = Endpoint.QueueMessage(TwMessageUpdateNetwork.CreateGrantRequest()).Response;
            status = response is TwMessageUpdateNetwork grant ? grant.Status : UpdateGrantStatus.Failed;
            return response.Type;
        }

        internal void ReleaseUpdateNetwork()
        {
            _ = Endpoint.QueueMessage(TwMessageUpdateNetwork.CreateReleaseRequest()).Response;
        }

        public MessageType SwitchFirewallMode(FirewallMode mode)
        {
            return Endpoint.QueueMessage(TwMessageModeSwitch.CreateRequest(mode)).Response.Type;
        }

        public MessageType RequestServerStop()
        {
            return Endpoint.QueueMessage(TwMessageSimple.CreateRequest(MessageType.STOP_SERVICE)).Response.Type;
        }

        public bool IsServerLocked
        {
            get
            {
                var resp = Endpoint.QueueMessage(TwMessageIsLocked.CreateRequest()).Response;
                if (resp is TwMessageIsLocked isLockedResp)
                    return isLockedResp.LockedStatus;
                else
                    return false;
            }
        }

        public MessageType SetPassphrase(string pwd)
        {
            return Endpoint.QueueMessage(TwMessageSetPassword.CreateRequest(pwd)).Response.Type;
        }

        public MessageType TryUnlockServer(string pwd)
        {
            return Endpoint.QueueMessage(TwMessageUnlock.CreateRequest(pwd)).Response.Type;
        }

        public MessageType LockServer()
        {
            return Endpoint.QueueMessage(TwMessageSimple.CreateRequest(MessageType.LOCK)).Response.Type;
        }

        public string TryGetProcessPath(uint pid)
        {
            var resp = Endpoint.QueueMessage(TwMessageGetProcessPath.CreateRequest(pid)).Response;
            if (resp.Type == MessageType.GET_PROCESS_PATH)
            {
                var respArgs = (TwMessageGetProcessPath)resp;
                return respArgs.Path;
            }
            else
                return string.Empty;
        }
    }
}
