using System.IO.Pipelines;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Gt5pOnlineServer.Services;

// pdistd half-closes its send side after empty POSTs, while still reading the
// response. Kestrel's socket transport reports that FIN as ConnectionClosed;
// forwarding it immediately aborts the otherwise complete HTTP request.
public static class GracefulReadEof
{
    public static ListenOptions UseGracefulReadEof(this ListenOptions listener, int gracefulSocketCloseSeconds = 0)
    {
        if (gracefulSocketCloseSeconds is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(gracefulSocketCloseSeconds));
        listener.Use(next => async connection =>
        {
            var originalTransport = connection.Transport;
            var originalClosed = connection.ConnectionClosed;
            using var requestClosed = new CancellationTokenSource();
            using var stopPump = new CancellationTokenSource();
            var input = new Pipe(new PipeOptions(
                pauseWriterThreshold: 65536, resumeWriterThreshold: 32768,
                useSynchronizationContext: false));
            var output = new AckDrainWriter(originalTransport.Output, connection.Features.Get<IConnectionSocketFeature>()?.Socket, gracefulSocketCloseSeconds, requestClosed.Token);
            connection.Transport = new ReadEofPipe(new BufferedEofReader(input.Reader), output);
            connection.ConnectionClosed = requestClosed.Token;
            // Kestrel's socket output is a PipeWriter: its reader completes on
            // a forced transport abort, even after the input already ended.
            // The callback is deprecated for arbitrary PipeWriter types, but is
            // supported by the .NET socket transport used by these listeners.
#pragma warning disable CS0618
            originalTransport.Output.OnReaderCompleted((_, state) =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { }
            }, requestClosed);
#pragma warning restore CS0618
            void PrepareGracefulClose()
            {
                output.PeerEndedSending();
            }
            var pump = PumpInput(originalTransport.Input, input.Writer, requestClosed,
                stopPump.Token, PrepareGracefulClose);
            try
            {
                await next(connection);
            }
            finally
            {
                try
                {
                    await output.CompleteAsync();
                }
                finally
                {
                    stopPump.Cancel();
                    try
                    {
                        await pump;
                        await input.Reader.CompleteAsync();
                    }
                    finally
                    {
                        connection.Transport = originalTransport;
                        connection.ConnectionClosed = originalClosed;
                    }
                }
            }
        });
        return listener;
    }

    private static async Task PumpInput(PipeReader source, PipeWriter destination,
        CancellationTokenSource requestClosed, CancellationToken stopping, Action prepareGracefulClose)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var result = await source.ReadAsync(stopping);
                var buffer = result.Buffer;
                try
                {
                    foreach (var segment in buffer)
                    {
                        var remaining = segment;
                        while (!remaining.IsEmpty)
                        {
                            // Bound each allocation even if the transport returns a large segment.
                            var count = Math.Min(remaining.Length, 16384);
                            var flushed = await destination.WriteAsync(remaining[..count], stopping);
                            remaining = remaining[count..];
                            if (flushed.IsCanceled || flushed.IsCompleted) return;
                        }
                    }
                    if (result.IsCompleted)
                    {
                        prepareGracefulClose();
                        return;
                    }
                    if (result.IsCanceled) stopping.ThrowIfCancellationRequested();
                }
                finally { source.AdvanceTo(buffer.End); }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            failure = error;
            requestClosed.Cancel();
        }
        finally
        {
            // EOF still reaches the HTTP parser: incomplete headers/bodies fail normally.
            // Actual input errors also cancel the request; only graceful FIN is deferred.
            await destination.CompleteAsync(failure);
            await source.CompleteAsync(failure);
        }
    }

    private sealed class ReadEofPipe(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private sealed class BufferedEofReader(PipeReader reader) : PipeReader
    {
        private SequencePosition _completedBufferEnd;
        private bool _maskedCompletion;
        private bool _exposeCompletion;

        // Kestrel's Content-Length reader treats transport EOF as a truncated
        // body before inspecting its bytes. Deliver those bytes before EOF.
        private ReadResult BufferedFirst(ReadResult result)
        {
            _maskedCompletion = result.IsCompleted && !result.Buffer.IsEmpty && !_exposeCompletion;
            if (!_maskedCompletion) return result;
            _completedBufferEnd = result.Buffer.End;
            return new ReadResult(result.Buffer, result.IsCanceled, isCompleted: false);
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) =>
            BufferedFirst(await reader.ReadAsync(cancellationToken));

        public override bool TryRead(out ReadResult result)
        {
            if (!reader.TryRead(out result)) return false;
            result = BufferedFirst(result);
            return true;
        }

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            // An incomplete header can examine the final bytes without consuming
            // them. On its next read expose EOF so the parser rejects it promptly.
            if (_maskedCompletion && examined.Equals(_completedBufferEnd) && !consumed.Equals(_completedBufferEnd))
                _exposeCompletion = true;
            _maskedCompletion = false;
            reader.AdvanceTo(consumed, examined);
        }
        public override void CancelPendingRead() => reader.CancelPendingRead();
        public override void Complete(Exception? exception = null) => reader.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => reader.CompleteAsync(exception);
    }
}
