using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace SocialPresence.RealtimePaint;

public sealed class PaintBoard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object gate = new();
    private readonly int[] cells = new int[CanvasProtocol.Width * CanvasProtocol.Height];
    private readonly List<BoardClient> clients = [];
    private long sequence;
    private long disconnectedSlowClients;

    public BoardClient? Connect()
    {
        lock (gate)
        {
            if (clients.Count >= CanvasProtocol.MaximumClients)
            {
                return null;
            }

            var client = new BoardClient();
            BoardSnapshot snapshot = new(
                "snapshot",
                sequence,
                CanvasProtocol.Width,
                CanvasProtocol.Height,
                [.. cells]);
            client.Messages.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions));
            clients.Add(client);
            return client;
        }
    }

    public void Disconnect(BoardClient client)
    {
        lock (gate)
        {
            clients.Remove(client);
            client.Complete();
        }
    }

    public bool TryApply(DrawRequest request)
    {
        if (request.Type != "stroke" ||
            request.Color <= 0 ||
            request.Color >= CanvasProtocol.Colors.Length ||
            request.Points is not { Length: > 0 and <= CanvasProtocol.MaximumPointsPerStroke })
        {
            return false;
        }

        foreach (PaintPoint point in request.Points)
        {
            if (point.X < 0 ||
                point.X >= CanvasProtocol.Width ||
                point.Y < 0 ||
                point.Y >= CanvasProtocol.Height)
            {
                return false;
            }
        }

        lock (gate)
        {
            for (int index = 0; index < request.Points.Length; index++)
            {
                PaintPoint point = request.Points[index];
                if (index == 0)
                {
                    SetPixel(point.X, point.Y, request.Color);
                    continue;
                }

                PaintPoint previous = request.Points[index - 1];
                DrawLine(previous.X, previous.Y, point.X, point.Y, request.Color);
            }

            byte[] message = JsonSerializer.SerializeToUtf8Bytes(
                new StrokeEvent(
                    "stroke",
                    ++sequence,
                    request.Color,
                    request.Points,
                    Stopwatch.GetTimestamp()),
                PaintJsonContext.Default.StrokeEvent);

            for (int index = clients.Count - 1; index >= 0; index--)
            {
                BoardClient client = clients[index];
                if (!client.Messages.Writer.TryWrite(message))
                {
                    clients.RemoveAt(index);
                    client.EvictSlow();
                    disconnectedSlowClients++;
                }
            }
        }

        return true;
    }

    public BoardSnapshot GetSnapshot()
    {
        lock (gate)
        {
            return new BoardSnapshot(
                "snapshot",
                sequence,
                CanvasProtocol.Width,
                CanvasProtocol.Height,
                [.. cells]);
        }
    }

    public int ConnectedClients
    {
        get
        {
            lock (gate)
            {
                return clients.Count;
            }
        }
    }

    public long DisconnectedSlowClients
    {
        get
        {
            lock (gate)
            {
                return disconnectedSlowClients;
            }
        }
    }

    private void SetPixel(int x, int y, int color)
    {
        cells[(y * CanvasProtocol.Width) + x] = color;
    }

    private void DrawLine(int x0, int y0, int x1, int y1, int color)
    {
        int deltaX = Math.Abs(x1 - x0);
        int stepX = x0 < x1 ? 1 : -1;
        int deltaY = -Math.Abs(y1 - y0);
        int stepY = y0 < y1 ? 1 : -1;
        int error = deltaX + deltaY;

        while (true)
        {
            SetPixel(x0, y0, color);
            if (x0 == x1 && y0 == y1)
            {
                return;
            }

            int doubledError = 2 * error;
            if (doubledError >= deltaY)
            {
                error += deltaY;
                x0 += stepX;
            }

            if (doubledError <= deltaX)
            {
                error += deltaX;
                y0 += stepY;
            }
        }
    }
}

public sealed class BoardClient
{
    private const int QueueCapacity = 8_192;
    private readonly TaskCompletionSource<bool> termination = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Channel<byte[]> Messages { get; } = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public Task<bool> Termination => termination.Task;
    public bool IsSlow => termination.Task.IsCompletedSuccessfully && termination.Task.Result;

    internal void Complete()
    {
        Messages.Writer.TryComplete();
        termination.TrySetResult(false);
    }

    internal void EvictSlow()
    {
        Messages.Writer.TryComplete();
        termination.TrySetResult(true);
    }
}
