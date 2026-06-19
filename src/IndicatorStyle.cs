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
    public abstract void Apply(IntPtr hwnd, SessionStatus status, PermissionMode mode, int size, Point location);

    // ── Built-in styles ──────────────────────────────────────────────────────

    public static readonly IndicatorStyle None    = new NoneIndicatorStyle();
    public static readonly IndicatorStyle Squares = new SpriteIndicatorStyle("Squares",
        LoadSprite("square-sleep.png"),
        LoadSprite("square-working.png"),
        LoadSprite("square-alert.png"));
    public static readonly IndicatorStyle Ducks   = new SpriteIndicatorStyle("Ducks",
        LoadSprite("duck-sleep.png"),
        LoadSprite("duck-working.png"),
        LoadSprite("duck-alert.png"));
    public static readonly IndicatorStyle Cats    = new SpriteIndicatorStyle("Cats",
        LoadSprite("cat-sleep.png"),
        LoadSprite("cat-working.png"),
        LoadSprite("cat-alert.png"));
    public static readonly IndicatorStyle Wolfenstein = new SpriteIndicatorStyle("Wolfenstein",
        LoadSprite("wolfenstein-sleep.png"),
        LoadSprite("wolfenstein-working.png"),
        LoadSprite("wolfenstein-alert.png"),
        new Dictionary<PermissionMode, Bitmap>
        {
            [PermissionMode.AcceptEdits] = LoadSprite("wolfenstein-working-accept-edits.png"),
            [PermissionMode.Plan]        = LoadSprite("wolfenstein-working-plan-mode.png"),
            [PermissionMode.Auto]        = LoadSprite("wolfenstein-working-auto.png"),
            [PermissionMode.Bypass]      = LoadSprite("wolfenstein-working-bypass.png"),
        });

    public static IReadOnlyList<IndicatorStyle> All { get; } = [None, Squares, Ducks, Cats, Wolfenstein];

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
    public override void Apply(IntPtr hwnd, SessionStatus status, PermissionMode mode, int size, Point location) { }
}

/// <summary>
/// Renders a set of three status sprites onto a layered window.
/// To add a new sprite pack, construct an instance with your own bitmaps
/// and add it to IndicatorStyle.All (or manage it externally).
/// </summary>
internal sealed class SpriteIndicatorStyle : IndicatorStyle
{
    private readonly Bitmap _idle, _active, _attention;
    private readonly IReadOnlyDictionary<PermissionMode, Bitmap> _activeByMode;

    public SpriteIndicatorStyle(string name, Bitmap idle, Bitmap active, Bitmap attention,
        IReadOnlyDictionary<PermissionMode, Bitmap>? activeByMode = null)
    {
        Name          = name;
        _idle         = idle;
        _active       = active;
        _attention    = attention;
        _activeByMode = activeByMode ?? new Dictionary<PermissionMode, Bitmap>();
    }

    public override string Name { get; }

    public override void Apply(IntPtr hwnd, SessionStatus status, PermissionMode mode, int size, Point location) =>
        NativeMethods.ApplyLayeredBitmap(hwnd, StatusSprite(status, mode), size, location);

    private Bitmap StatusSprite(SessionStatus s, PermissionMode mode) => s switch
    {
        SessionStatus.Running        => _activeByMode.TryGetValue(mode, out var m) ? m : _active,
        SessionStatus.NeedsAttention => _attention,
        SessionStatus.AwaitingInput  => _attention,
        _                            => _idle,
    };
}
