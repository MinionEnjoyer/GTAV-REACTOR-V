using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ReactorV.BootstrapHost;

namespace ReactorV.BootstrapAttachmentValidation
{
    internal static class Program
    {
        private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(30);

        private static int Main()
        {
            using var watchdog = new Timer(_ => Environment.FailFast(
                "Bootstrap attachment validation exceeded its process-wide watchdog."),
                null, WatchdogTimeout, Timeout.InfiniteTimeSpan);
            try
            {
                DelayedAcknowledgement();
                NeverAcknowledgingPeerTimesOutAndClosesClient();
                PartialFrameTimesOutAndClosesClient();
                DisposedPeerUnblocksOutstandingRead();
                Console.WriteLine("RESULT PASS: bootstrap-attachment-validation target=net48");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("RESULT FAIL: bootstrap-attachment-validation " + error);
                return 1;
            }
        }

        private static void DelayedAcknowledgement()
        {
            using var pair = new PipePair();
            var writer = Task.Run(() =>
            {
                Thread.Sleep(75);
                BootstrapHostWire.Write(pair.Server, ReadyAcknowledgement(), 1000);
            });
            var acknowledgement = BootstrapHostWire.Read(pair.Client, 1000);
            writer.GetAwaiter().GetResult();
            if (acknowledgement?.Value<int?>("generation") != 7)
                throw new InvalidDataException("Delayed acknowledgement was not received.");
            Console.WriteLine("CASE PASS: delayedACK");
        }

        private static void NeverAcknowledgingPeerTimesOutAndClosesClient()
        {
            using var pair = new PipePair();
            var stopwatch = Stopwatch.StartNew();
            ExpectTimeout(() => BootstrapHostWire.Read(pair.Client, 125));
            if (stopwatch.ElapsedMilliseconds > 2000 || !pair.ClientSafeDisposed())
                throw new InvalidOperationException("Never-ACK read did not close the client within its deadline.");
            Console.WriteLine("CASE PASS: neverACK");
        }

        private static void PartialFrameTimesOutAndClosesClient()
        {
            using var pair = new PipePair();
            var partialRead = Task.Run(() => CaptureException(() => BootstrapHostWire.Read(pair.Client, 125)));
            var partialWriter = Task.Run(() =>
            {
                var declaredLength = BitConverter.GetBytes(32);
                pair.Server.Write(declaredLength, 0, declaredLength.Length);
                pair.Server.WriteByte((byte)'{');
                pair.Server.Flush();
            });
            var error = partialRead.GetAwaiter().GetResult();
            if (!(error is TimeoutException))
                throw new InvalidOperationException("Partial-frame read did not time out.", error);
            try { partialWriter.GetAwaiter().GetResult(); } catch (IOException) { }
            if (!pair.ClientSafeDisposed())
                throw new InvalidOperationException("Partial-frame timeout did not close the client.");
            Console.WriteLine("CASE PASS: partialframe");
        }

        private static void DisposedPeerUnblocksOutstandingRead()
        {
            using var pair = new PipePair();
            var waiting = Task.Run(() => CaptureException(() => BootstrapHostWire.Read(pair.Client, 5000)));
            Thread.Sleep(75);
            pair.Client.Dispose();
            var error = waiting.GetAwaiter().GetResult();
            if (error != null && !(error is IOException) && !(error is ObjectDisposedException) &&
                !(error is OperationCanceledException) && !(error is TimeoutException))
                throw new InvalidOperationException("Disposing a peer did not unblock the production bounded read.", error);
            Console.WriteLine("CASE PASS: disposedpeer");
        }

        private static void ExpectTimeout(Action action)
        {
            try { action(); } catch (TimeoutException) { return; }
            throw new InvalidOperationException("Expected the bounded production helper to time out.");
        }

        private static Exception? CaptureException(Action action)
        {
            try { action(); return null; } catch (Exception error) { return error; }
        }

        private static JObject ReadyAcknowledgement() => new JObject
        {
            ["type"] = "hello_ack",
            ["protocol"] = BootstrapHostHandshake.ProtocolVersion,
            ["generation"] = 7,
            ["ready"] = true,
        };

        private sealed class PipePair : IDisposable
        {
            public PipePair()
            {
                var name = "ReactorV.BootstrapAttachmentValidation." + Guid.NewGuid().ToString("N");
                Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                Client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                var accepting = Task.Run(() => Server.WaitForConnection());
                Client.Connect(2000);
                accepting.GetAwaiter().GetResult();
            }

            public NamedPipeServerStream Server { get; }
            public NamedPipeClientStream Client { get; }

            public bool ClientSafeDisposed()
            {
                try { return Client.SafePipeHandle.IsClosed; }
                catch (ObjectDisposedException) { return true; }
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }
    }
}
