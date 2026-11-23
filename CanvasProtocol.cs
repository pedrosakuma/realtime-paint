namespace SocialPresence.RealtimePaint;

public sealed record PaintPoint(int X, int Y);

public sealed record DrawRequest(string Type, int Color, PaintPoint[]? Points);

public sealed record BoardSnapshot(string Type, long Sequence, int Width, int Height, int[] Cells);

public sealed record StrokeEvent(
    string Type,
    long Sequence,
    int Color,
    PaintPoint[] Points,
    long PublishedTimestamp);

public static class CanvasProtocol
{
    public const int Width = 256;
    public const int Height = 256;
    public const int MaximumPointsPerStroke = 512;
    public const int MaximumClients = 64;

    public static readonly string[] Colors =
    [
        "#ffffff",
        "#111827",
        "#ef4444",
        "#22c55e",
        "#3b82f6",
        "#f59e0b"
    ];
}
