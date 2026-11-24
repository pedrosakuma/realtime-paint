using System.Text.Json;
using SocialPresence.RealtimePaint;

namespace SocialPresence.RealtimePaint.Tests;

public sealed class StrokeSerializationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(CanvasProtocol.MaximumPointsPerStroke)]
    public void PublishedStrokeMatchesWebSerializer(int pointCount)
    {
        var board = new PaintBoard();
        BoardClient client = Assert.IsType<BoardClient>(board.Connect());
        Assert.True(client.Messages.Reader.TryRead(out _));

        PaintPoint[] points = Enumerable.Range(0, pointCount)
            .Select(index => new PaintPoint(index % CanvasProtocol.Width, index % CanvasProtocol.Height))
            .ToArray();
        Assert.True(board.TryApply(new DrawRequest("stroke", 1, points)));
        Assert.True(client.Messages.Reader.TryRead(out byte[]? message));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        StrokeEvent? stroke = JsonSerializer.Deserialize<StrokeEvent>(message, options);
        Assert.NotNull(stroke);
        Assert.Equal(1, stroke.Sequence);
        Assert.Equal(points, stroke.Points);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(stroke, options), message);
        board.Disconnect(client);
    }
}
