using SocialPresence.RealtimePaint.Load;

namespace SocialPresence.RealtimePaint.Tests;

public sealed class DrawingWorkloadTests
{
    [Fact]
    public void AdjacentSegmentsProduceExpectedBoardAcrossClientsAndRounds()
    {
        const int clients = 3;
        const int segments = 170;
        var board = new PaintBoard();
        Assert.True(board.TryApply(new DrawRequest("stroke", 5, [new PaintPoint(255, 255)])));
        int[] initial = board.GetSnapshot().Cells;

        for (int round = 0; round < 2; round++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                for (int client = 0; client < clients; client++)
                {
                    DrawRequest request = DrawingWorkload.Segment(client, clients, segment);
                    PaintPoint[] points = Assert.IsType<PaintPoint[]>(request.Points);
                    Assert.Equal(1, Math.Abs(points[0].X - points[1].X) + Math.Abs(points[0].Y - points[1].Y));
                    Assert.All(points, point =>
                    {
                        Assert.InRange(point.X, client * CanvasProtocol.Width / clients,
                            ((client + 1) * CanvasProtocol.Width / clients) - 1);
                        Assert.InRange(point.Y, 0, CanvasProtocol.Height - 1);
                    });
                    Assert.True(board.TryApply(request));
                }
            }
        }

        Assert.Equal(DrawingWorkload.ExpectedCells(initial, clients, segments), board.GetSnapshot().Cells);
    }
}
