using System.Net.WebSockets;
using System.Text.Json;
using SocialPresence.RealtimePaint;

namespace SocialPresence.RealtimePaint.Tests;

public sealed class SlowClientTests
{
    [Fact]
    public async Task OverflowAbortsStalledSocketWithoutInterruptingHealthyClient()
    {
        var board = new PaintBoard();
        using var socket = new StalledSocket();
        Task connection = PaintConnection.RunAsync(socket, board, CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BoardClient healthy = Assert.IsType<BoardClient>(board.Connect());
        Assert.True(healthy.Messages.Reader.TryRead(out _));

        try
        {
            for (int i = 0; i < 8_193; i++)
            {
                Assert.True(board.TryApply(new DrawRequest("stroke", 1, [new PaintPoint(0, 0)])));
                Assert.True(healthy.Messages.Reader.TryRead(out _));
            }

            Assert.Equal(1, board.DisconnectedSlowClients);
            Assert.Equal(1, board.ConnectedClients);
            await socket.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await connection.WaitAsync(TimeSpan.FromSeconds(10));

            BoardClient reconnected = Assert.IsType<BoardClient>(board.Connect());
            Assert.True(reconnected.Messages.Reader.TryRead(out byte[]? snapshotBytes));
            BoardSnapshot? snapshot = JsonSerializer.Deserialize<BoardSnapshot>(
                snapshotBytes,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(snapshot);
            Assert.Equal(8_193, snapshot.Sequence);
            Assert.Equal(1, snapshot.Cells[0]);
            board.Disconnect(reconnected);
        }
        finally
        {
            socket.Abort();
            try
            {
                await connection.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }

            board.Disconnect(healthy);
        }
    }

    [Fact]
    public async Task NormalCloseDoesNotAbortSocket()
    {
        var board = new PaintBoard();
        using var socket = new StalledSocket();
        Task connection = PaintConnection.RunAsync(socket, board, CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        socket.CloseFromPeer();
        await connection.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.False(socket.Aborted.Task.IsCompleted);
        Assert.Equal(0, board.ConnectedClients);
        Assert.Equal(0, board.DisconnectedSlowClients);
    }

    private sealed class StalledSocket : WebSocket
    {
        private readonly CancellationTokenSource aborted = new();
        private readonly TaskCompletionSource peerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile WebSocketState state = WebSocketState.Open;

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            if (state == WebSocketState.Aborted)
            {
                return;
            }

            state = WebSocketState.Aborted;
            aborted.Cancel();
            Aborted.TrySetResult();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public void CloseFromPeer() => peerClosed.TrySetResult();

        public override void Dispose() => aborted.Dispose();

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aborted.Token);
            await peerClosed.Task.WaitAsync(linked.Token);
            state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, aborted.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
        }
    }
}
