namespace XiControl.Ui;

/// <summary>Единая позиция всех OSD: центр карточки находится на 80% рабочей области.</summary>
internal static class OsdPlacement
{
    private const double VerticalCenterRatio = 0.80;

    public static Point BottomCenter(Rectangle workingArea, Size card)
    {
        int x = workingArea.Left + (workingArea.Width - card.Width) / 2;
        int desiredY = workingArea.Top + (int)Math.Round(workingArea.Height * VerticalCenterRatio)
            - card.Height / 2;
        int maxY = Math.Max(workingArea.Top, workingArea.Bottom - card.Height);
        return new Point(x, Math.Clamp(desiredY, workingArea.Top, maxY));
    }
}
