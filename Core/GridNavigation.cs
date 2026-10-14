using System;

namespace Kobold.Core
{
    /// <summary>The four arrow directions over the folder item grid.</summary>
    public enum GridDirection { Left, Right, Up, Down }

    /// <summary>
    /// The arrow-key motion over the item grid: rows fill left to right, so
    /// Left/Right are the neighbouring cell (the two row ends meet), while
    /// Up/Down jump a full row of <c>columns</c>; a partial last row takes
    /// Down from above on its last entry. A move that would leave the grid
    /// keeps the current cell.
    /// </summary>
    public static class GridNavigation
    {
        public static int Move(int index, int count, int columns, GridDirection direction)
        {
            if (count <= 0 || index < 0 || index >= count) return index;
            if (columns < 1) return index;

            switch (direction)
            {
                case GridDirection.Left:
                    return index > 0 ? index - 1 : index;
                case GridDirection.Right:
                    return index < count - 1 ? index + 1 : index;
                case GridDirection.Up:
                    return index - columns >= 0 ? index - columns : index;
                case GridDirection.Down:
                    {
                        int target = index + columns;
                        if (target < count) return target;
                        return index / columns == (count - 1) / columns ? index : count - 1;
                    }
                default:
                    return index;
            }
        }
    }
}
