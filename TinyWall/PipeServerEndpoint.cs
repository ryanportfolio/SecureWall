using System;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using pylorak.TinyWall.Prompting;
using pylorak.Utilities;

namespace pylorak.TinyWall
{
    internal delegate TwMessage PipeDataReceived(TwMessage req);

    internal class PipeServerEndpoint : Disposable
    {
        private readonly Thread m_PipeWorkerThread;
        private readonly PipeDataReceived m_RcvCallback;
        private readonly string m_PipeName;
        private readonly object m_ServerSyncRoot = new();
        private NamedPipeServerStream? m_ActiveServer;

        private volatile bool m_Run = true;

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
                return;

            m_Run = false;
            lock (m_ServerSyncRoot)
            {
                // Disposing the active server cancels WaitForConnection without relying
                // on the caller's token being allowed through the pipe ACL.
                m_ActiveServer?.Dispose();
            }

            if (disposing)
            {
                // Release managed resources
                m_PipeWorkerThread.Join(TimeSpan.FromMilliseconds(1000));
            }

            // Release unmanaged resources.
            // Set large fields to null.
            // Call Dispose on your base class.
            base.Dispose(disposing);
        }

        internal PipeServerEndpoint(PipeDataReceived recvCallback, string serverPipeName)
        {
            m_RcvCallback = recvCallback;
            m_PipeName = serverPipeName;

            m_PipeWorkerThread = new Thread(new ThreadStart(PipeServerWorker))
            {
                Name = "ServerPipeWorker",
                IsBackground = true
            };
            m_PipeWorkerThread.Start();
        }

        private void PipeServerWorker()
        {
            // Allow authenticated users access to the pipe
            SecurityIdentifier AuthenticatedSID = new(WellKnownSidType.AuthenticatedUserSid, null);
            PipeAccessRule par = new(AuthenticatedSID, PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow);
            PipeSecurity ps = new();
            ps.AddAccessRule(par);

            while (m_Run)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    // Create pipe server
                    pipeServer = new NamedPipeServerStream(m_PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.WriteThrough, 2048 * 10, 2048 * 10, ps);
                    lock (m_ServerSyncRoot)
                    {
                        if (!m_Run)
                            break;
                        m_ActiveServer = pipeServer;
                    }

                    if (!pipeServer.IsConnected)
                    {
                        pipeServer.WaitForConnection();
                        pipeServer.ReadMode = PipeTransmissionMode.Message;

                        if (!AuthAsServer(pipeServer))
                            throw new InvalidOperationException("Client authentication failed.");
                    }

                    var req = SerializationHelper.DeserializeFromPipe<TwMessage>(pipeServer, 3000, TwMessageComError.Instance);
                    var resp = m_RcvCallback(req);
                    SerializationHelper.SerializeToPipe(pipeServer, resp);
                }
                catch
                {
                    if (m_Run)
                        Thread.Sleep(200);
                }
                finally
                {
                    lock (m_ServerSyncRoot)
                    {
                        if (ReferenceEquals(m_ActiveServer, pipeServer))
                            m_ActiveServer = null;
                    }
                    pipeServer?.Dispose();
                }
            } //while
        }

        private static bool AuthAsServer(PipeStream stream)
        {
            if (!Utils.SafeNativeMethods.GetNamedPipeClientProcessId(stream.SafePipeHandle.DangerousGetHandle(), out ulong clientPid))
                return false;

            string clientFilePath = Utils.GetPathOfProcess((uint)clientPid);

            return PipeClientAuthorization.IsExpectedExecutable(
                clientFilePath,
                pylorak.Windows.ProcessManager.ExecutablePath);
        }
    }
}
