using System.Net.WebSockets;
using System.Text.Json;

namespace SocialPresence.RealtimePaint;

public static class PaintConnection
{
    public static async Task RunAsync(WebSocket socket, PaintBoard board, CancellationToken cancellationToken)
    {
        BoardClient? client = board.Connect();
        if (client is null)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "The board has reached its client limit.",
                cancellationToken);
            return;
        }

        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task sender = SendMessagesAsync(client, socket, sendCancellation.Token);
        Task termination = AbortSlowClientAsync(client, socket);
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
        string closeDescription = "Connection closed.";

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                byte[]? payload = await ReceiveMessageAsync(socket, cancellationToken);
                if (payload is null)
                {
                    break;
                }

                DrawRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<DrawRequest>(
                        payload,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                }
                catch (JsonException)
                {
                    request = null;
                }

                if (request is null || !board.TryApply(request))
                {
                    closeStatus = WebSocketCloseStatus.InvalidPayloadData;
                    closeDescription = "Invalid stroke message.";
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (client.IsSlow)
        {
        }
        catch (WebSocketException) when (client.IsSlow)
        {
        }
        finally
        {
            board.Disconnect(client);
            sendCancellation.Cancel();
            await termination;
            try
            {
                await sender;
            }
            catch (OperationCanceledException) when (sendCancellation.IsCancellationRequested)
            {
            }
            catch (WebSocketException) when (client.IsSlow)
            {
            }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(
                    closeStatus,
                    closeDescription,
                    CancellationToken.None);
            }
        }
    }

    private static async Task AbortSlowClientAsync(BoardClient client, WebSocket socket)
    {
        if (await client.Termination)
        {
            socket.Abort();
        }
    }

    private static async Task SendMessagesAsync(
        BoardClient client,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        await foreach (byte[] message in client.Messages.Reader.ReadAllAsync(cancellationToken))
        {
            await socket.SendAsync(
                message,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private static async Task<byte[]?> ReceiveMessageAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4_096];
        using var message = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(
                buffer,
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Only text WebSocket messages are supported.");
            }

            if (message.Length + result.Count > 32_768)
            {
                throw new InvalidDataException("Stroke message exceeds the size limit.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }
}
