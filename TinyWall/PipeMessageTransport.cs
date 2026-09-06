using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

namespace pylorak.TinyWall
{
    // One bounded message, including its total duration, before allocating/deserializing state.
    internal static class PipeMessageTransport
    {
        internal const int MaximumMessageBytes = 8 * 1024 * 1024;

        internal static void Write(PipeStream pipe, TwMessage message, int timeoutMilliseconds)
        {
            byte[] bytes = SerializationHelper.Serialize(message);
            if (bytes.Length > MaximumMessageBytes)
                throw new IOException("Pipe message exceeds the size limit.");
            AwaitBounded(pipe, pipe.WriteAsync(bytes, 0, bytes.Length), timeoutMilliseconds);
        }

        internal static TwMessage Read(PipeStream pipe, int timeoutMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            var buffer = new byte[4096];
            using var result = new MemoryStream();
            do
            {
                Task<int> read = pipe.ReadAsync(buffer, 0, buffer.Length);
                AwaitBounded(pipe, read, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                int count = read.GetAwaiter().GetResult();
                if (count == 0 || result.Length + count > MaximumMessageBytes)
                    throw new IOException("Pipe closed or message exceeds the size limit.");
                result.Write(buffer, 0, count);
            } while (!pipe.IsMessageComplete);
            return SerializationHelper.Deserialize(result.ToArray(), (TwMessage)TwMessageComError.Instance);
        }

        internal static void AcknowledgeResponse(PipeStream pipe)
        {
            AwaitBounded(pipe, pipe.WriteAsync(new byte[] { 1 }, 0, 1), 3000);
        }

        internal static void WaitForResponseAcknowledgement(PipeStream pipe)
        {
            var buffer = new byte[1];
            Task<int> read = pipe.ReadAsync(buffer, 0, 1);
            AwaitBounded(pipe, read, 3000);
            if (read.GetAwaiter().GetResult() != 1 || buffer[0] != 1 || !pipe.IsMessageComplete)
                throw new IOException("Invalid pipe response acknowledgement.");
        }

        private static void AwaitBounded(PipeStream pipe, Task operation, int remainingMilliseconds)
        {
            if (remainingMilliseconds <= 0 ||
                Task.WhenAny(operation, Task.Delay(Math.Max(1, remainingMilliseconds))).GetAwaiter().GetResult() != operation)
            {
                pipe.Dispose();
                _ = operation.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("Pipe exchange exceeded its deadline.");
            }
            operation.GetAwaiter().GetResult();
        }
    }
}
