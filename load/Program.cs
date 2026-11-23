using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using SocialPresence.RealtimePaint;
using SocialPresence.RealtimePaint.Load;

LoadOptions options = LoadOptions.Parse(args);
using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl) };
Scenario scenario = await http.GetFromJsonAsync<Scenario>("scenario")
    ?? throw new InvalidDataException("The scenario endpoint returned no data.");
if (scenario.ConnectedClients != 0)
{
    throw new InvalidOperationException("The load run requires a server without other connected clients.");
}

int uniqueStrokes = options.Clients * options.StrokesPerClient;
long totalStrokes = (long)uniqueStrokes * options.Rounds;
if (uniqueStrokes > CanvasProtocol.Width * CanvasProtocol.Height)
{
    throw new ArgumentOutOfRangeException(
        nameof(options.StrokesPerClient),
        "The run must fit unique strokes in the board to verify final state.");
}

var clients = new List<LoadClient>(options.Clients);
var slowClients = new List<SlowLoadClient>(options.SlowClients);
Uri webSocketUri = new(options.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal)
    .Replace("https://", "wss://", StringComparison.Ordinal) + "ws");

try
{
    for (int index = 0; index < options.Clients; index++)
    {
        var client = new LoadClient(index, webSocketUri, totalStrokes);
        clients.Add(client);
        await client.ConnectAsync();
    }

    for (int index = 0; index < options.SlowClients; index++)
    {
        var client = new SlowLoadClient(webSocketUri);
        slowClients.Add(client);
        await client.ConnectAsync();
    }

    BoardSnapshot initialSnapshot = clients[0].InitialSnapshot
        ?? throw new InvalidDataException("The load client did not receive an initial snapshot.");
    foreach (BoardSnapshot? snapshot in clients.Select(client => client.InitialSnapshot)
                 .Concat(slowClients.Select(client => client.InitialSnapshot)))
    {
        if (snapshot is null ||
            snapshot.Sequence != initialSnapshot.Sequence ||
            snapshot.Width != initialSnapshot.Width ||
            snapshot.Height != initialSnapshot.Height ||
            !snapshot.Cells.SequenceEqual(initialSnapshot.Cells))
        {
            throw new InvalidDataException("Clients received different initial board snapshots.");
        }
    }

    var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task[] senders = clients.Select(
        client => client.SendStrokesAsync(
            options.Clients,
            options.StrokesPerClient,
            options.Rounds,
            options.IntervalMilliseconds,
            start.Task)).ToArray();
    Stopwatch stopwatch = Stopwatch.StartNew();
    start.SetResult();
    await Task.WhenAll(senders);
    await Task.WhenAll(clients.Select(client => client.WaitForCompletionAsync()));
    stopwatch.Stop();

    BoardSnapshot finalState = await http.GetFromJsonAsync<BoardSnapshot>("state")
        ?? throw new InvalidDataException("The state endpoint returned no data.");
    int[] expectedCells = DrawingWorkload.ExpectedCells(
        initialSnapshot.Cells,
        options.Clients,
        options.StrokesPerClient);
    long expectedSequence = initialSnapshot.Sequence + totalStrokes;
    if (finalState.Sequence != expectedSequence || !finalState.Cells.SequenceEqual(expectedCells))
    {
        throw new InvalidDataException(
            $"Board invariant failed: sequence={finalState.Sequence}, expected={expectedSequence}, " +
            $"cellsMatch={finalState.Cells.SequenceEqual(expectedCells)}.");
    }

    foreach (LoadClient client in clients)
    {
        if (client.InitialSnapshot?.Sequence != initialSnapshot.Sequence ||
            client.ReceivedStrokes != totalStrokes ||
            client.LastSequence != expectedSequence)
        {
            throw new InvalidDataException(
                $"Client {client.Index} received {client.ReceivedStrokes}/{totalStrokes} " +
                $"events; final sequence={client.LastSequence}.");
        }
    }

    Scenario finalScenario = await http.GetFromJsonAsync<Scenario>("scenario")
        ?? throw new InvalidDataException("The scenario endpoint returned no data.");
    if (finalScenario.ProcessId != scenario.ProcessId)
    {
        throw new InvalidDataException("The server process changed during the load run.");
    }

    long slowDisconnections = finalScenario.DisconnectedSlowClients - scenario.DisconnectedSlowClients;
    string[] slowDisconnectOutcomes = [];
    if (options.RequireSlowDisconnect)
    {
        if (slowDisconnections != options.SlowClients)
        {
            throw new InvalidDataException(
                $"Expected {options.SlowClients} slow-client disconnections, observed {slowDisconnections}.");
        }

        slowDisconnectOutcomes = await Task.WhenAll(slowClients.Select(client => client.WaitForDisconnectAsync()));
        await Task.WhenAll(slowClients.Select(client => client.VerifyReconnectAsync(finalState)));
    }

    long[] deliveryLatencyHistogram = new long[LoadClient.LatencyBucketCount];
    long deliveredEvents = 0;
    double totalDeliveryLatency = 0;
    double maxDeliveryLatency = 0;
    foreach (LoadClient client in clients)
    {
        deliveredEvents += client.ReceivedStrokes;
        totalDeliveryLatency += client.TotalDeliveryLatencyMilliseconds;
        maxDeliveryLatency = Math.Max(maxDeliveryLatency, client.MaxDeliveryLatencyMilliseconds);
        for (int bucket = 0; bucket < deliveryLatencyHistogram.Length; bucket++)
        {
            deliveryLatencyHistogram[bucket] += client.DeliveryLatencyHistogram[bucket];
        }
    }

    LoadResult result = new(
        "social-presence.realtime-paint-load/v2",
        DateTimeOffset.UtcNow,
        scenario.ProcessId,
        options.Clients,
        options.SlowClients,
        options.StrokesPerClient,
        options.Rounds,
        options.IntervalMilliseconds,
        totalStrokes,
        stopwatch.Elapsed.TotalSeconds,
        totalStrokes / stopwatch.Elapsed.TotalSeconds,
        deliveredEvents,
        totalDeliveryLatency / deliveredEvents,
        PercentileMilliseconds(deliveryLatencyHistogram, deliveredEvents, 0.50),
        PercentileMilliseconds(deliveryLatencyHistogram, deliveredEvents, 0.95),
        PercentileMilliseconds(deliveryLatencyHistogram, deliveredEvents, 0.99),
        maxDeliveryLatency,
        slowDisconnections,
        options.RequireSlowDisconnect,
        slowDisconnectOutcomes,
        options.RequireSlowDisconnect ? slowClients.Count : 0,
        Convert.ToHexString(SHA256.HashData(ToLittleEndianBytes(finalState.Cells))),
        true);
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    if (options.OutputPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
        await File.WriteAllTextAsync(options.OutputPath, json);
    }

    Console.WriteLine(json);
}
finally
{
    try
    {
        await Task.WhenAll(clients.Select(client => client.DisposeAsync().AsTask()));
    }
    finally
    {
        foreach (SlowLoadClient client in slowClients)
        {
            client.Dispose();
        }
    }
}

return 0;

static byte[] ToLittleEndianBytes(int[] values)
{
    byte[] bytes = new byte[values.Length * sizeof(int)];
    for (int index = 0; index < values.Length; index++)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(index * sizeof(int), sizeof(int)),
            values[index]);
    }

    return bytes;
}

static double? PercentileMilliseconds(long[] histogram, long sampleCount, double percentile)
{
    long targetRank = (long)Math.Ceiling(sampleCount * percentile);
    long observed = 0;
    for (int bucket = 0; bucket < histogram.Length; bucket++)
    {
        observed += histogram[bucket];
        if (observed >= targetRank)
        {
            return bucket == histogram.Length - 1 ? null : bucket / 10.0;
        }
    }

    throw new InvalidDataException("The delivery-latency histogram is incomplete.");
}

sealed class LoadClient(int index, Uri uri, long expectedStrokes) : IAsyncDisposable
{
    public const int LatencyBucketCount = 100_002;

    private readonly ClientWebSocket socket = new();
    private readonly TaskCompletionSource initialSnapshot = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource allStrokesReceived = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? receiver;
    private long lastSequence;
    private int receivedStrokes;
    private readonly long[] deliveryLatencyHistogram = new long[LatencyBucketCount];

    public int Index { get; } = index;
    public BoardSnapshot? InitialSnapshot { get; private set; }
    public int ReceivedStrokes => Volatile.Read(ref receivedStrokes);
    public long LastSequence => Volatile.Read(ref lastSequence);
    public long[] DeliveryLatencyHistogram => deliveryLatencyHistogram;
    public double TotalDeliveryLatencyMilliseconds { get; private set; }
    public double MaxDeliveryLatencyMilliseconds { get; private set; }

    public async Task ConnectAsync()
    {
        await socket.ConnectAsync(uri, CancellationToken.None);
        receiver = ReceiveAsync();
        await initialSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (InitialSnapshot is null ||
            InitialSnapshot.Width != CanvasProtocol.Width ||
            InitialSnapshot.Height != CanvasProtocol.Height ||
            InitialSnapshot.Cells is not { Length: CanvasProtocol.Width * CanvasProtocol.Height })
        {
            throw new InvalidDataException($"Client {Index} received an invalid initial board.");
        }
    }

    public async Task SendStrokesAsync(
        int clients,
        int strokesPerRound,
        int rounds,
        int intervalMilliseconds,
        Task start)
    {
        await start;
        for (int round = 0; round < rounds; round++)
        {
            for (int stroke = 0; stroke < strokesPerRound; stroke++)
            {
                DrawRequest request = DrawingWorkload.Segment(Index, clients, stroke);
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                    request,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await socket.SendAsync(
                    payload,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    CancellationToken.None);
                if (intervalMilliseconds > 0)
                {
                    await Task.Delay(intervalMilliseconds);
                }
            }
        }
    }

    public async Task WaitForCompletionAsync()
    {
        await allStrokesReceived.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Load run complete.",
                    CancellationToken.None);
            }

            if (receiver is not null)
            {
                await receiver;
            }
        }
        finally
        {
            socket.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        byte[] buffer = new byte[8_192];
        using var message = new MemoryStream();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(
                    buffer,
                    CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(message.ToArray());
                string? type = document.RootElement.GetProperty("type").GetString();
                if (type == "snapshot")
                {
                    InitialSnapshot = JsonSerializer.Deserialize<BoardSnapshot>(
                        document.RootElement.GetRawText(),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    Interlocked.Exchange(ref lastSequence, InitialSnapshot?.Sequence ?? 0);
                    initialSnapshot.TrySetResult();
                }
                else if (type == "stroke")
                {
                    long sequence = document.RootElement.GetProperty("sequence").GetInt64();
                    long publishedTimestamp = document.RootElement
                        .GetProperty("publishedTimestamp")
                        .GetInt64();
                    double deliveryLatency = Stopwatch.GetElapsedTime(publishedTimestamp).TotalMilliseconds;
                    if (deliveryLatency < 0)
                    {
                        throw new InvalidDataException(
                            "A publication timestamp was ahead of the local monotonic clock.");
                    }

                    int latencyBucket = (int)Math.Clamp(
                        Math.Ceiling(deliveryLatency * 10),
                        0,
                        LatencyBucketCount - 1);
                    deliveryLatencyHistogram[latencyBucket]++;
                    TotalDeliveryLatencyMilliseconds += deliveryLatency;
                    MaxDeliveryLatencyMilliseconds = Math.Max(
                        MaxDeliveryLatencyMilliseconds,
                        deliveryLatency);
                    if (InitialSnapshot is null)
                    {
                        throw new InvalidDataException("A stroke arrived before the initial snapshot.");
                    }

                    long expected = Interlocked.Increment(ref lastSequence);
                    if (sequence != expected)
                    {
                        throw new InvalidDataException(
                            $"Client {Index} received sequence {sequence}, expected {expected}.");
                    }

                    if (Interlocked.Increment(ref receivedStrokes) == expectedStrokes)
                    {
                        allStrokesReceived.TrySetResult();
                    }
                }

                message.SetLength(0);
            }

            if (ReceivedStrokes != expectedStrokes)
            {
                var exception = new EndOfStreamException(
                    $"Client {Index} disconnected after {ReceivedStrokes} events.");
                initialSnapshot.TrySetException(exception);
                allStrokesReceived.TrySetException(exception);
            }
        }
        catch (Exception exception)
        {
            initialSnapshot.TrySetException(exception);
            allStrokesReceived.TrySetException(exception);
            throw;
        }
    }
}

sealed class SlowLoadClient(Uri uri) : IDisposable
{
    private readonly ClientWebSocket socket = new();

    public BoardSnapshot? InitialSnapshot { get; private set; }

    public async Task ConnectAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(uri, timeout.Token);
        InitialSnapshot = await ReadSnapshotAsync(socket, timeout.Token);
    }

    public async Task VerifyReconnectAsync(BoardSnapshot expected)
    {
        using var reconnected = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await reconnected.ConnectAsync(uri, timeout.Token);
        BoardSnapshot snapshot = await ReadSnapshotAsync(reconnected, timeout.Token);
        if (snapshot.Sequence != expected.Sequence ||
            snapshot.Width != expected.Width ||
            snapshot.Height != expected.Height ||
            !snapshot.Cells.SequenceEqual(expected.Cells))
        {
            throw new InvalidDataException("The reconnected slow client received an incorrect board snapshot.");
        }

        reconnected.Abort();
    }

    private static async Task<BoardSnapshot> ReadSnapshotAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8_192];
        using var snapshot = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("A slow client did not receive a text snapshot.");
            }

            snapshot.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                using JsonDocument document = JsonDocument.Parse(snapshot.ToArray());
                if (document.RootElement.GetProperty("type").GetString() != "snapshot")
                {
                    throw new InvalidDataException("A slow client did not receive an initial snapshot.");
                }

                BoardSnapshot result = JsonSerializer.Deserialize<BoardSnapshot>(
                    document.RootElement.GetRawText(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("A slow client received an invalid initial snapshot.");
                if (result.Width != CanvasProtocol.Width ||
                    result.Height != CanvasProtocol.Height ||
                    result.Cells is not { Length: CanvasProtocol.Width * CanvasProtocol.Height })
                {
                    throw new InvalidDataException("A slow client received an invalid board size.");
                }

                return result;
            }
        }
    }

    public async Task<string> WaitForDisconnectAsync()
    {
        byte[] buffer = new byte[8_192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return $"close:{socket.CloseStatus}";
                }
            }
        }
        catch (WebSocketException exception) when (socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
            return $"websocket-error:{exception.WebSocketErrorCode}";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The slow client's socket did not close after eviction.");
        }
    }

    public void Dispose()
    {
        socket.Abort();
        socket.Dispose();
    }
}

sealed record Scenario(int ProcessId, int ConnectedClients, long DisconnectedSlowClients);

sealed record LoadResult(
    string Schema,
    DateTimeOffset CompletedAtUtc,
    int ProcessId,
    int Clients,
    int SlowClients,
    int StrokesPerClient,
    int Rounds,
    int IntervalMilliseconds,
    long BroadcastEvents,
    double ElapsedSeconds,
    double EventsPerSecond,
    long DeliveredEvents,
    double AverageDeliveryLatencyMilliseconds,
    double? P50DeliveryLatencyMilliseconds,
    double? P95DeliveryLatencyMilliseconds,
    double? P99DeliveryLatencyMilliseconds,
    double MaximumDeliveryLatencyMilliseconds,
    long SlowClientDisconnections,
    bool SlowDisconnectRequired,
    string[] SlowDisconnectOutcomes,
    int SlowReconnectsVerified,
    string FinalBoardSha256,
    bool BoardInvariantPassed);

sealed record LoadOptions(
    string BaseUrl,
    int Clients,
    int SlowClients,
    int StrokesPerClient,
    int Rounds,
    int IntervalMilliseconds,
    bool RequireSlowDisconnect,
    string? OutputPath)
{
    public static LoadOptions Parse(string[] args)
    {
        string baseUrl = "http://127.0.0.1:5088/";
        int clients = 4;
        int slowClients = 0;
        int strokesPerClient = 1_000;
        int rounds = 1;
        int intervalMilliseconds = 0;
        bool requireSlowDisconnect = false;
        string? output = null;

        for (int index = 0; index < args.Length; index++)
        {
            string value = index + 1 < args.Length
                ? args[++index]
                : throw new ArgumentException($"Missing value for {args[index]}.");
            switch (args[index - 1])
            {
                case "--base-url":
                    baseUrl = value.EndsWith('/') ? value : value + "/";
                    break;
                case "--clients":
                    clients = ParseBoundedInt("--clients", value, 1, CanvasProtocol.MaximumClients);
                    break;
                case "--slow-clients":
                    slowClients = ParseBoundedInt("--slow-clients", value, 0, CanvasProtocol.MaximumClients);
                    break;
                case "--strokes-per-client":
                    strokesPerClient = ParseBoundedInt(
                        "--strokes-per-client",
                        value,
                        1,
                        CanvasProtocol.Width * CanvasProtocol.Height);
                    break;
                case "--rounds":
                    rounds = ParseBoundedInt("--rounds", value, 1, 100);
                    break;
                case "--interval-ms":
                    intervalMilliseconds = ParseBoundedInt("--interval-ms", value, 0, 60_000);
                    break;
                case "--require-slow-disconnect":
                    if (!bool.TryParse(value, out requireSlowDisconnect))
                    {
                        throw new ArgumentException("--require-slow-disconnect must be true or false.");
                    }
                    break;
                case "--output":
                    output = value;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index - 1]}");
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            !baseUri.IsLoopback)
        {
            throw new ArgumentException(
                "The load client requires a loopback URL because delivery latency uses the local monotonic clock.");
        }

        if (clients + slowClients > CanvasProtocol.MaximumClients)
        {
            throw new ArgumentException("Normal and slow clients together must not exceed the server limit.");
        }

        if (requireSlowDisconnect && slowClients == 0)
        {
            throw new ArgumentException("--require-slow-disconnect requires at least one slow client.");
        }

        long uniqueStrokes = (long)clients * strokesPerClient;
        if (uniqueStrokes > CanvasProtocol.Width * CanvasProtocol.Height)
        {
            throw new ArgumentException(
                "clients × strokes-per-client must not exceed the board's pixel count.");
        }

        return new LoadOptions(
            baseUrl,
            clients,
            slowClients,
            strokesPerClient,
            rounds,
            intervalMilliseconds,
            requireSlowDisconnect,
            output);
    }

    private static int ParseBoundedInt(string name, string value, int minimum, int maximum)
    {
        if (!int.TryParse(value, out int result) || result < minimum || result > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Value must be from {minimum} to {maximum}.");
        }

        return result;
    }
}
