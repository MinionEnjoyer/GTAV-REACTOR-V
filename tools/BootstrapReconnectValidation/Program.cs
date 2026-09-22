using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RageWebUI.Core;
using ReactorV.BootstrapHost;

namespace ReactorV.BootstrapReconnectValidation
{
    internal static class Program
    {
        private const int HostGeneration = 7;

        private static int Main()
        {
            var fakeGtaPid = Process.GetCurrentProcess().Id + 1000000;
            var runtimeType = typeof(BridgeBroker).Assembly
                .GetReferencedAssemblies();
            // Load the concrete runtime directly so this tool exercises the
            // production private type without widening its visibility.
            var runtimeAssembly = Assembly.Load("RageWebUI.Runtime");
            var type = runtimeAssembly.GetType(
                "RageWebUI.Runtime.BootstrapOverlayRuntime", true);
            var ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic, null,
                new[] { typeof(int), typeof(string), typeof(BridgeBroker), typeof(bool) }, null);
            if (ctor == null) throw new MissingMethodException(type.FullName, ".ctor");

            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset,
                BootstrapHostNames.ReadyEvent(fakeGtaPid)))
            {
                ready.Set();
                var first = new HostSession(fakeGtaPid);
                first.Start();
                var runtime = ctor.Invoke(new object[]
                {
                    fakeGtaPid, Environment.CurrentDirectory, new BridgeBroker(), false,
                });
                try
                {
                    if (!(bool)type.GetMethod("Start")!.Invoke(runtime, null)!)
                        throw new InvalidOperationException("Initial bootstrap attachment failed.");
                    var firstPublicGeneration = ReadyGeneration(type, runtime);
                    if (firstPublicGeneration <= 0)
                        throw new InvalidOperationException("Initial readiness was not published.");

                    first.Dispose(); // EOF; no PumpInput or telemetry call follows.
                    using (var second = new HostSession(fakeGtaPid))
                    {
                        second.Start();
                        var secondPublicGeneration = WaitForReadyGeneration(
                            type, runtime, firstPublicGeneration, 5000);
                        if (secondPublicGeneration == firstPublicGeneration)
                            throw new InvalidOperationException(
                                "Reconnect reused the Script-visible readiness generation.");

                        // The public generation is local; the lease wire frame
                        // must still carry the host's unchanged generation.
                        type.GetMethod("AdvanceRuntimeReadyHandoff")!.Invoke(
                            runtime, new object[] { secondPublicGeneration });
                        var lease = second.ReadLease(2000);
                        if (lease.Value<int?>("generation") != HostGeneration)
                            throw new InvalidOperationException("Lease was not translated to host generation.");
                        second.AcknowledgeLease(lease);
                        var ackWait = Stopwatch.StartNew();
                        var acknowledged = false;
                        while (ackWait.ElapsedMilliseconds < 2000)
                        {
                            var state = type.GetMethod("AdvanceRuntimeReadyHandoff")!.Invoke(
                                runtime, new object[] { secondPublicGeneration });
                            if (state?.ToString() == "Signaled") { acknowledged = true; break; }
                            Thread.Sleep(10);
                        }
                        if (!acknowledged) throw new TimeoutException("Translated lease ACK was not accepted.");
                        Console.WriteLine("CASE PASS: eof-autonomous-reconnect-local-epoch");

                        ((IDisposable)runtime).Dispose();
                    }

                    using (var afterDispose = new HostSession(fakeGtaPid))
                    {
                        afterDispose.Start();
                        if (afterDispose.WaitForConnection(1200))
                            throw new InvalidOperationException("Disposed runtime reconnected.");
                    }
                    Console.WriteLine("CASE PASS: dispose-stops-reconnect");
                    Console.WriteLine("RESULT PASS: bootstrap-reconnect-validation target=net48");
                    return 0;
                }
                finally { ((IDisposable)runtime).Dispose(); first.Dispose(); }
            }
        }

        private static int ReadyGeneration(Type type, object runtime)
        {
            var args = new object[] { 0 };
            var ready = (bool)type.GetMethod("TryGetReadyContentGeneration")!
                .Invoke(runtime, args)!;
            return ready ? (int)args[0] : 0;
        }

        private static int WaitForReadyGeneration(Type type, object runtime, int prior, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                var generation = ReadyGeneration(type, runtime);
                if (generation > 0 && generation != prior) return generation;
                Thread.Sleep(25);
            }
            throw new TimeoutException("Autonomous reconnect did not publish a fresh ready generation.");
        }

        private sealed class HostSession : IDisposable
        {
            private readonly NamedPipeServerStream _pipe;
            private Task? _connected;

            public HostSession(int fakeGtaPid)
            {
                _pipe = new NamedPipeServerStream(BootstrapHostNames.Pipe(fakeGtaPid),
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }

            public void Start()
            {
                _connected = _pipe.WaitForConnectionAsync();
                Task.Run(() =>
                {
                    _connected!.GetAwaiter().GetResult();
                    var hello = BootstrapHostWire.Read(_pipe);
                    if (!BootstrapHostHandshake.TryValidateHello(hello,
                        hello!.Value<int>("pid"), out var reason))
                        throw new InvalidOperationException(reason);
                    BootstrapHostWire.Write(_pipe,
                        BootstrapHostHandshake.CreateReadyAcknowledgement(HostGeneration, true));
                    BootstrapHostWire.Write(_pipe, new JObject
                    {
                        ["type"] = "state", ["protocol"] = BootstrapHostHandshake.ProtocolVersion,
                        ["generation"] = HostGeneration, ["ready"] = true,
                        ["visible"] = false, ["surface"] = "none",
                    });
                });
            }

            public bool WaitForConnection(int milliseconds) =>
                _connected != null && _connected.Wait(milliseconds);

            public JObject ReadLease(int milliseconds)
            {
                var read = Task.Run(() => BootstrapHostWire.Read(_pipe));
                if (!read.Wait(milliseconds)) throw new TimeoutException("Lease request timed out.");
                return read.GetAwaiter().GetResult() ?? throw new InvalidOperationException("Lease EOF.");
            }

            public void AcknowledgeLease(JObject lease) => BootstrapHostWire.Write(_pipe,
                BootstrapHostHandshake.CreateRuntimeReadyLeaseAcknowledgement(
                    HostGeneration, lease.Value<string>("requestId")!, true, true));

            public void Dispose() { try { _pipe.Dispose(); } catch { } }
        }
    }
}
