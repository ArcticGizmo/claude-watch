namespace ClaudeWatch;

/// <summary>
/// Controls how session indicators are rendered. Extend by creating a new subclass
/// or constructing a new SpriteIndicatorStyle with different images.
/// </summary>
internal abstract class IndicatorStyle
{
    public abstract string Name { get; }

    /// <summary>False means no indicator forms are shown at all for this style.</summary>
    public virtual bool ShowForms => true;

    /// <summary>Paint the session indicator onto the layered window handle.</summary>
    public abstract void Apply(IntPtr hwnd, SessionStatus status, int size, Point location);

    // ── Built-in styles ──────────────────────────────────────────────────────

    public static readonly IndicatorStyle None    = new NoneIndicatorStyle();
    public static readonly IndicatorStyle Squares = new SquaresIndicatorStyle();
    public static readonly IndicatorStyle Ducks   = new SpriteIndicatorStyle("Ducks",
        LoadSprite("duck-sleep.png"),
        LoadSprite("duck-working.png"),
        LoadSprite("duck-alert.png"));
    public static readonly IndicatorStyle Cats    = new SpriteIndicatorStyle("Cats",
        LoadSprite("cat-sleep.png"),
        LoadSprite("cat-working.png"),
        LoadSprite("cat-alert.png"));

    public static IReadOnlyList<IndicatorStyle> All { get; } = [None, Squares, Ducks, Cats];

    public static IndicatorStyle FromName(string name) =>
        All.FirstOrDefault(s => s.Name == name) ?? Ducks;

    protected static Bitmap LoadSprite(string filename)
    {
        using var stream = System.Reflection.Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"ClaudeWatch.sprites.{filename}")!;
        return new Bitmap(stream);
    }
}

// ── Implementations ──────────────────────────────────────────────────────────

internal sealed class NoneIndicatorStyle : IndicatorStyle
{
    public override string Name    => "None";
    public override bool ShowForms => false;
    public override void Apply(IntPtr hwnd, SessionStatus status, int size, Point location) { }
}

internal sealed class SquaresIndicatorStyle : IndicatorStyle
{
    private static readonly Color RunningColor   = Color.FromArgb(34,  197,  94);
    private static readonly Color AttentionColor = Color.FromArgb(251, 146,  60);
    private static readonly Color IdleColor      = Color.FromArgb(100, 116, 139);

    public override string Name => "Squares";

    public override void Apply(IntPtr hwnd, SessionStatus status, int size, Point location)
    {
        using var bmp = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, StatusColor(status));
        NativeMethods.ApplyLayeredBitmap(hwnd, bmp, size, location);
    }

    private static Color StatusColor(SessionStatus s) => s switch
    {
        SessionStatus.Running        => RunningColor,
        SessionStatus.NeedsAttention => AttentionColor,
        _                            => IdleColor,
    };
}

/// <summary>
/// Renders a set of three status sprites onto a layered window.
/// To add a new sprite pack, construct an instance with your own bitmaps
/// and add it to IndicatorStyle.All (or manage it externally).
/// </summary>
internal sealed class SpriteIndicatorStyle : IndicatorStyle
{
    private readonly Bitmap _idle, _active, _attention;

    public SpriteIndicatorStyle(string name, Bitmap idle, Bitmap active, Bitmap attention)
    {
        Name       = name;
        _idle      = idle;
        _active    = active;
        _attention = attention;
    }

    public override string Name { get; }

    public override void Apply(IntPtr hwnd, SessionStatus status, int size, Point location) =>
        NativeMethods.ApplyLayeredBitmap(hwnd, StatusSprite(status), size, location);

    private Bitmap StatusSprite(SessionStatus s) => s switch
    {
        SessionStatus.Running        => _active,
        SessionStatus.NeedsAttention => _attention,
        _                            => _idle,
    };
}
