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

            // Retain the first instance across normal exchanges to reserve the pipe name.
            // If another process owns the name, creation fails and clients reject its identity.
            while (m_Run)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = PipeServerFactory.Create(m_PipeName, ps);
                    lock (m_ServerSyncRoot)
                    {
                        if (!m_Run)
                            break;
                        m_ActiveServer = pipeServer;
                    }

                    while (m_Run)
                    {
                        string stage = "connect";
                        bool reusable = false;
                        try
                        {
                            pipeServer.WaitForConnection();
                            pipeServer.ReadMode = PipeTransmissionMode.Message;
                            stage = "authenticate";
                            if (!AuthAsServer(pipeServer))
                                throw new InvalidOperationException("Client authentication failed.");
                            stage = "read";
                            var req = PipeMessageTransport.Read(pipeServer, 3000);
                            stage = "callback";
                            var resp = m_RcvCallback(req);
                            stage = "write";
                            PipeMessageTransport.Write(pipeServer, resp, 3000);
                            // Disconnect discards unread output. Wait for the client's bounded ACK.
                            stage = "acknowledge";
                            PipeMessageTransport.WaitForResponseAcknowledgement(pipeServer);
                        }
                        catch (Exception exception)
                        {
                            _ = stage;
                            _ = exception;
#if DEBUG
                            if (PipeServerIdentity.IsSelfTestPipe(m_PipeName))
                                PipeClientEndpoint.RecordSelfTestFailure("server." + stage, exception);
#endif
                        }
                        finally
                        {
                            // A client that leaves before sending bytes marks the
                            // stream Broken, so IsConnected is false. Reset that
                            // native instance too, or every future connect fails.
                            // If reset fails, the outer loop disposes and recreates it.
                            reusable = TryResetForNextClient(pipeServer);
                        }
                        if (!reusable)
                            break;
                    }
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
            }
        }

        internal static bool TryResetForNextClient(NamedPipeServerStream pipe)
        {
            try
            {
                pipe.Disconnect();
                return true;
            }
            catch (InvalidOperationException) { return false; } // Includes disposed streams.
            catch (System.IO.IOException) { return false; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

        private static bool AuthAsServer(PipeStream stream)
        {
            if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out uint clientPid))
                return false;

            string clientFilePath = Utils.GetPathOfProcess((uint)clientPid);

            return PipeClientAuthorization.IsExpectedExecutable(
                clientFilePath,
                pylorak.Windows.ProcessManager.ExecutablePath);
        }
    }
}
