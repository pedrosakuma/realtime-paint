using SocialPresence.RealtimePaint;

namespace SocialPresence.RealtimePaint.Load;

public static class DrawingWorkload
{
    public static DrawRequest Segment(int client, int clients, int segment)
    {
        int left = client * CanvasProtocol.Width / clients;
        int right = (client + 1) * CanvasProtocol.Width / clients;
        int stripeWidth = right - left;
        int position = segment % ((stripeWidth * CanvasProtocol.Height) - 1);

        return new DrawRequest(
            "stroke",
            1 + (client % (CanvasProtocol.Colors.Length - 1)),
            [PointAt(left, stripeWidth, position), PointAt(left, stripeWidth, position + 1)]);
    }

    public static int[] ExpectedCells(int[] initialCells, int clients, int segmentsPerClient)
    {
        int[] cells = [.. initialCells];
        for (int client = 0; client < clients; client++)
        {
            for (int segment = 0; segment < segmentsPerClient; segment++)
            {
                DrawRequest request = Segment(client, clients, segment);
                foreach (PaintPoint point in request.Points!)
                {
                    cells[(point.Y * CanvasProtocol.Width) + point.X] = request.Color;
                }
            }
        }

        return cells;
    }

    private static PaintPoint PointAt(int left, int width, int position)
    {
        int row = position / width;
        int column = position % width;
        return new PaintPoint(left + (row % 2 == 0 ? column : width - 1 - column), row);
    }
}
