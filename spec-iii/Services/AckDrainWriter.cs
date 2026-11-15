using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Sockets;

namespace Gt5pOnlineServer.Services;

// Defer transport completion until the queued
// response has been transmitted and acknowledged, bounded by the existing budget.
internal sealed class AckDrainWriter(PipeWriter inner, Socket? socket, int seconds, CancellationToken aborted) : PipeWriter
{
    private readonly object gate = new();
    private Task? completion;
    private long bytesWritten;
    private int gracefulEof;
    private readonly ulong? initialBytesOut = ReadTcpInfo(socket)?.BytesOut;
    public Task Completion { get { lock (gate) return completion ?? Task.CompletedTask; } }
    public void PeerEndedSending() => Volatile.Write(ref gracefulEof, 1);
    public override void Advance(int bytes) { inner.Advance(bytes); Interlocked.Add(ref bytesWritten, bytes); }
    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    public override void CancelPendingFlush() => inner.CancelPendingFlush();
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
    public override long UnflushedBytes => inner.UnflushedBytes;
    public override void Complete(Exception? exception = null) { lock(gate) completion ??= Finish(exception); }
    public override ValueTask CompleteAsync(Exception? exception = null) { Complete(exception); return new ValueTask(Completion); }

    private async Task Finish(Exception? exception)
    {
        try
        {
            if (exception is null && Volatile.Read(ref gracefulEof) != 0 && seconds > 0 && !aborted.IsCancellationRequested)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                budget.CancelAfter(TimeSpan.FromSeconds(seconds));
                var clock = Stopwatch.StartNew();
                await inner.FlushAsync(budget.Token);
                var expected = (ulong)Interlocked.Read(ref bytesWritten);
                var info = ReadTcpInfo(socket);
                if (initialBytesOut is null || info is null)
                    await Task.Delay(Math.Min(seconds * 1000, 50), budget.Token);
                else
                {
                    while (clock.ElapsedMilliseconds < seconds * 1000)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        info = ReadTcpInfo(socket);
                        if (info is null)
                        {
                            await Task.Delay(50, budget.Token);
                            break;
                        }
                        if (info.Value.BytesOut >= initialBytesOut.Value + expected && info.Value.BytesInFlight == 0)
                            break;
                        await Task.Delay(5, budget.Token);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        finally { await inner.CompleteAsync(exception); }
    }

    // TCP_INFO_v0: DWORD State/Mss (0/4); ULONG64 ConnectionTimeMs (8);
    // BOOLEAN TimestampsEnabled (16), padding to20; RTT(20),minRTT(24),
    // DWORD BytesInFlight(28); Cwnd/SndWnd/RcvWnd/RcvBuf(32..44);
    // ULONG64 BytesOut(48),BytesIn(56); remainder counters through88.
    internal static (uint BytesInFlight, ulong BytesOut)? ReadTcpInfo(Socket? socket)
    {
        if (!OperatingSystem.IsWindows() || socket is null) return null;
        try
        {
            byte[] data = new byte[128];
            int count = socket.IOControl(unchecked((int)0xD8000027), BitConverter.GetBytes(0u), data);
            return count >= 64 ? (BitConverter.ToUInt32(data,28), BitConverter.ToUInt64(data,48)) : null;
        }
        catch (SocketException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }
}
