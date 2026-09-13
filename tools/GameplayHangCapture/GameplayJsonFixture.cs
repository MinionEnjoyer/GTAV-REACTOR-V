using System;
using System.IO;
using System.Text;
using System.Threading;

// Owned, finite-lived file activity only. No game, collector or process control.
public sealed class GameplayJsonFixture : IDisposable
{
    private readonly ManualResetEvent stop = new ManualResetEvent(false);
    private readonly Thread thread;
    private FileStream held;
    public int Writes;
    public Exception Failure;

    public GameplayJsonFixture(string path, bool exclusive, int releaseAfterMilliseconds)
        : this(path, exclusive, releaseAfterMilliseconds, false) { }

    public GameplayJsonFixture(string path, bool exclusive, int releaseAfterMilliseconds, bool byteRangeLock)
    {
        held = File.Open(path, FileMode.Open, FileAccess.ReadWrite,
            exclusive ? FileShare.None : FileShare.ReadWrite | FileShare.Delete);
        if (byteRangeLock) held.Lock(0, held.Length);
        thread = new Thread(() => {
            stop.WaitOne(releaseAfterMilliseconds);
            held.Dispose();
        });
        thread.IsBackground = true;
        thread.Start();
    }

    public GameplayJsonFixture(string path, int replacements)
    {
        thread = new Thread(() => {
            try
            {
                for (int i = 1; i <= replacements && !stop.WaitOne(1); i++)
                {
                    string temp = path + ".new-" + Guid.NewGuid().ToString("N");
                    File.WriteAllText(temp, "{\"sequence\":" + i +
                        ",\"mirror\":" + i + "}", new UTF8Encoding(false));
                    File.Replace(temp, path, null);
                    Interlocked.Increment(ref Writes);
                }
            }
            catch (Exception error) { Failure = error; }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    public void Dispose()
    {
        stop.Set();
        if (!thread.Join(5000)) throw new TimeoutException("Owned JSON fixture did not stop.");
        stop.Dispose();
    }
}
