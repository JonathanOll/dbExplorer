namespace DbExplorer.Core.Plans;

/// <summary>Where one operator sits on the plan canvas (top-left corner and size, in canvas units).</summary>
public sealed record PlanBox(PlanNode Node, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Lays a plan out like SSMS does: the root on the left, every operator to the right of its parent, data flowing right to
/// left. A leaf takes the next free row; a parent sits level with the middle of its children. Pure geometry.
/// </summary>
public sealed class PlanLayout
{
    private PlanLayout(IReadOnlyList<PlanBox> boxes, double width, double height)
    {
        Boxes = boxes;
        Width = width;
        Height = height;
        ByNode = boxes.ToDictionary(b => b.Node);
    }

    public IReadOnlyList<PlanBox> Boxes { get; }

    public Dictionary<PlanNode, PlanBox> ByNode { get; }

    public double Width { get; }

    public double Height { get; }

    public static PlanLayout Compute(PlanNode? root, double nodeWidth = 188, double nodeHeight = 74, double gapX = 76, double gapY = 18, double margin = 24)
    {
        if (root is null) return new PlanLayout([], margin * 2, margin * 2);

        var boxes = new List<PlanBox>();
        var nextRow = 0.0;
        double Place(PlanNode node, int depth)
        {
            double centre;
            if (node.Children.Count == 0)
            {
                centre = nextRow + nodeHeight / 2;
                nextRow += nodeHeight + gapY;
            }
            else
            {
                var centres = node.Children.Select(c => Place(c, depth + 1)).ToList();
                centre = (centres[0] + centres[^1]) / 2;
            }
            boxes.Add(new PlanBox(node, margin + depth * (nodeWidth + gapX), margin + centre - nodeHeight / 2, nodeWidth, nodeHeight));
            return centre;
        }
        Place(root, 0);

        boxes.Sort((a, b) => a.Node.Id.CompareTo(b.Node.Id));
        var width = boxes.Max(b => b.Right) + margin;
        var height = boxes.Max(b => b.Y + b.Height) + margin;
        return new PlanLayout(boxes, width, height);
    }
}
