using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Threading;
using pylorak.Utilities;

namespace pylorak.TinyWall
{
    public class PipeClientEndpoint
    {
#if DEBUG
        private static readonly ConcurrentQueue<string> SelfTestFailures = new ConcurrentQueue<string>();
        internal static string DebugSelfTestFailures => string.Join(" | ", SelfTestFailures.ToArray());
        internal static void RecordSelfTestFailure(string stage, Exception exception)
        {
            SelfTestFailures.Enqueue(stage + ": " + exception.GetType().Name + " " + exception.HResult + " " + exception.Message);
            while (SelfTestFailures.Count > 16) SelfTestFailures.TryDequeue(out _);
        }
#endif
        private readonly object SenderSyncRoot = new();
        private readonly string m_PipeName;
        private readonly int m_TestServerProcessId = 0;

        public PipeClientEndpoint(string clientPipeName)
        {
            m_PipeName = clientPipeName;
        }

#if DEBUG
        internal PipeClientEndpoint(string clientPipeName, int expectedTestServerProcessId)
            : this(clientPipeName)
        {
            if (!PipeServerIdentity.IsSelfTestPipe(clientPipeName) ||
                expectedTestServerProcessId != System.Diagnostics.Process.GetCurrentProcess().Id)
                throw new ArgumentException("Expected the current process on a unique self-test pipe.");
            m_TestServerProcessId = expectedTestServerProcessId;
        }
#endif

        private void SendRequest(TwRequest req)
        {
            TwMessage ret = TwMessageComError.Instance;
            lock (SenderSyncRoot)
            {
                // In case of a communication error,
                // retry a small number of times.
                for (int i = 0; i < 2; ++i)
                {
                    var resp = SendRequest(req.Request);
                    if (resp.Type != MessageType.COM_ERROR)
                    {
                        ret = resp;
                        break;
                    }

                    Thread.Sleep(200);
                }
            }

            req.Response = ret;
        }

        private TwMessage SendRequest(TwMessage msg)
        {
            string stage = "connect";
            try
            {
                using var pipeClient = new NamedPipeClientStream (".", m_PipeName, PipeDirection.InOut, PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    System.Security.Principal.TokenImpersonationLevel.Identification);
                pipeClient.Connect(1000);
                pipeClient.ReadMode = PipeTransmissionMode.Message;
                stage = "authenticate";
                using var serverIdentity = PipeServerIdentity.Authenticate(pipeClient, m_PipeName, m_TestServerProcessId);

                // Send command
                stage = "write";
                PipeMessageTransport.Write(pipeClient, msg, 3000);

                // Get response
                stage = "read";
                TwMessage response = PipeMessageTransport.Read(pipeClient, 20000);
                stage = "acknowledge";
                PipeMessageTransport.AcknowledgeResponse(pipeClient);
                return response;
            }
            catch (Exception exception)
            {
                _ = stage;
                _ = exception;
#if DEBUG
                if (PipeServerIdentity.IsSelfTestPipe(m_PipeName)) RecordSelfTestFailure("client." + stage, exception);
#endif
                return TwMessageComError.Instance;
            }
        }

        public TwRequest QueueMessage(TwMessage msg)
        {
            var req = new TwRequest(msg);
            SendRequest(req);
            return req;
        }
    }
}
