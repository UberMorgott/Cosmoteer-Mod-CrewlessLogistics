using System;
using System.Collections.Generic;

namespace CrewlessLogistics.Belts;

/// <summary>Cell rectangle in part coordinates; <c>Contains</c> matches native <c>IntRect.Contains</c>.</summary>
internal readonly record struct CellRect(int X, int Y, int W, int H)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + W && y < Y + H;
}

/// <summary>One electrical socket: a physical boundary cell and the side facing its neighbor.</summary>
internal readonly record struct PortEdge(int X, int Y, string Direction)
{
    public string Name => $"CrewlessLogisticsPort_{Direction}_{X}_{Y}";
}

/// <summary>
/// Cable sockets on a part's perimeter (pure; ported from the retired PowerShell perimeter generator). Native reference:
/// the PartRules constructor builds ProhibitLeft/Right/Above/Below rectangles from Size, not PhysicalRect, and
/// IsCellProhibited matches category + IntRect.Contains. Neighbors inside the normal Size stay excluded because
/// barrels/exhaust may reserve nonphysical cells.
/// </summary>
internal static class PortGeometry
{
    /// <summary>Directional prohibition rectangles exactly as the native PartRules constructor expands them.</summary>
    public static List<(string Category, CellRect Rect)> SideRects(int width, int height, int left, int right, int above, int below, string[]? prohibits)
    {
        var rects = new List<(string, CellRect)>();
        void Side(string name, int distance, CellRect rect)
        {
            if (distance <= 0)
            {
                return;
            }
            if (prohibits == null)
            {
                throw new ArgumentException($"Prohibit{name} used but Prohibits wasn't specified, matching native PartRules validation.");
            }
            foreach (string category in prohibits)
            {
                rects.Add((category, rect));
            }
        }
        Side("Left", left, new CellRect(-left, 0, left, height));
        Side("Right", right, new CellRect(width, 0, right, height));
        Side("Above", above, new CellRect(0, -above, width, above));
        Side("Below", below, new CellRect(0, height, width, below));
        return rects;
    }

    /// <summary>
    /// Every physical perimeter edge whose outside neighbor lies outside Size and outside each prohibition rectangle
    /// whose category the cable is prohibited by. Fixed order (top/bottom per column, then left/right per row).
    /// </summary>
    public static List<PortEdge> Perimeter(int width, int height, CellRect? physicalRect, IReadOnlyList<(string Category, CellRect Rect)> prohibitRects, ICollection<string> cableProhibitedBy)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Part.Size must contain two positive cell dimensions.");
        }
        var size = new CellRect(0, 0, width, height);
        CellRect p = physicalRect ?? size;
        if (p.W <= 0 || p.H <= 0)
        {
            throw new ArgumentException("Part.PhysicalRect must have positive dimensions.");
        }
        if (p.X < 0 || p.Y < 0 || p.X + p.W > width || p.Y + p.H > height)
        {
            throw new ArgumentException("Part.PhysicalRect must be contained within Part.Size, as required by native PartRules.");
        }
        foreach (var (_, r) in prohibitRects)
        {
            if (r.W < 0 || r.H < 0)
            {
                throw new ArgumentException("ProhibitRects dimensions cannot be negative.");
            }
        }
        int lastX = p.X + p.W - 1, lastY = p.Y + p.H - 1;
        var edges = new List<PortEdge>();
        void Try(int x, int y, string direction, int nx, int ny)
        {
            if (size.Contains(nx, ny))
            {
                return;
            }
            foreach (var (category, rect) in prohibitRects)
            {
                if (cableProhibitedBy.Contains(category) && rect.Contains(nx, ny))
                {
                    return;
                }
            }
            edges.Add(new PortEdge(x, y, direction));
        }
        for (int x = p.X; x <= lastX; x++)
        {
            Try(x, p.Y, "Up", x, p.Y - 1);
            Try(x, lastY, "Down", x, lastY + 1);
        }
        for (int y = p.Y; y <= lastY; y++)
        {
            Try(p.X, y, "Left", p.X - 1, y);
            Try(lastX, y, "Right", lastX + 1, y);
        }
        return edges;
    }
}
