using System.Net.WebSockets;
using System.Text;
using SocialPresence.RealtimePaint;

namespace SocialPresence.RealtimePaint.Tests;

public sealed class ReceiveMessageTests
{
    [Fact]
    public async Task ReusedBufferHandlesFragmentedAndSubsequentShorterMessages()
    {
        string first = """{"type":"stroke","color":1,"points":[{"x":10,"y":10}]}""" + new string(' ', 5_000);
        string second = """{"type":"stroke","color":2,"points":[{"x":11,"y":11}]}""";
        using var socket = new ScriptedSocket(first, second);
        var board = new PaintBoard();

        await PaintConnection.RunAsync(socket, board, CancellationToken.None);

        BoardSnapshot snapshot = board.GetSnapshot();
        Assert.Equal(2, snapshot.Sequence);
        Assert.Equal(1, snapshot.Cells[10 * CanvasProtocol.Width + 10]);
        Assert.Equal(2, snapshot.Cells[11 * CanvasProtocol.Width + 11]);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Queue<(byte[] Bytes, bool End)> frames;
        private WebSocketState state = WebSocketState.Open;

        public ScriptedSocket(string first, string second)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(first);
            frames = new Queue<(byte[], bool)>(
            [
                (bytes[..3_000], false),
                (bytes[3_000..], true),
                (Encoding.UTF8.GetBytes(second), true)
            ]);
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;
        public override void Abort() => state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!frames.TryDequeue(out var frame))
            {
                state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }

            frame.Bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(frame.Bytes.Length, WebSocketMessageType.Text, frame.End));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
