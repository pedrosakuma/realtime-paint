using System.Net.WebSockets;
using System.Text.Json;
using SocialPresence.RealtimePaint;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddSingleton<PaintBoard>();

WebApplication app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet(
    "/scenario",
    (PaintBoard board) => Results.Ok(
        new
        {
            processId = Environment.ProcessId,
            implementation = "baseline",
            width = CanvasProtocol.Width,
            height = CanvasProtocol.Height,
            maximumClients = CanvasProtocol.MaximumClients,
            connectedClients = board.ConnectedClients,
            disconnectedSlowClients = board.DisconnectedSlowClients
        }));
app.MapGet("/state", (PaintBoard board) => Results.Ok(board.GetSnapshot()));
app.Map("/ws", async (HttpContext context, PaintBoard board) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    await PaintConnection.RunAsync(socket, board, context.RequestAborted);
});

Console.WriteLine(
    $"PAINT_READY pid={Environment.ProcessId} " +
    $"board={CanvasProtocol.Width}x{CanvasProtocol.Height} " +
    "state=volatile transport=websocket");

await app.RunAsync();
