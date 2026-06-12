using System.Drawing.Imaging;

namespace ClaudeWatch;

internal static class IconRenderer
{
    private static readonly Color ColorRunning   = Color.FromArgb(34, 197, 94);
    private static readonly Color ColorAttention = Color.FromArgb(251, 146, 60);
    private static readonly Color ColorIdle      = Color.FromArgb(148, 163, 184);
    private static readonly Color ColorNone      = Color.FromArgb(100, 116, 139);

    public static Icon Create(int sessionCount, SessionStatus worstStatus)
    {
        const int Size = 32;

        using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var bg = sessionCount == 0 ? ColorNone : worstStatus switch
            {
                SessionStatus.NeedsAttention => ColorAttention,
                SessionStatus.Running        => ColorRunning,
                _                            => ColorIdle,
            };

            using var bgBrush = new SolidBrush(bg);
            g.FillEllipse(bgBrush, 1, 1, Size - 2, Size - 2);

            var label    = sessionCount == 0 ? "–" : sessionCount > 9 ? "9+" : sessionCount.ToString();
            var fontSize = sessionCount > 9 ? 10f : 14f;

            using var font      = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var textSize = g.MeasureString(label, font);
            g.DrawString(label, font, textBrush,
                (Size - textSize.Width) / 2f,
                (Size - textSize.Height) / 2f);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var rawIcon = Icon.FromHandle(hIcon);
            using var ms = new MemoryStream();
            rawIcon.Save(ms);
            ms.Position = 0;
            return new Icon(ms);
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }
}
