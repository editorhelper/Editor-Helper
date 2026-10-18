using System.Windows.Media;
using System.Windows.Media.Effects;

namespace EditorHelper.Ui;

/// <summary>
/// Палитра Premium Cosmic (монохромная): глубокий чёрный космос, белые акценты
/// и градации серого. Значения совпадают с Resources/Styles.xaml, чтобы
/// динамически созданные элементы выглядели так же, как элементы из XAML.
/// </summary>
public static class Palette
{
    // Поверхности — от глубокого фона к приподнятым панелям
    public static readonly Brush BgPrimary = Frozen("#000000");
    public static readonly Brush BgSecondary = Frozen("#060606");
    public static readonly Brush BgTertiary = Frozen("#0B0B0B");
    public static readonly Brush BgElevated = Frozen("#121212");
    public static readonly Brush BgPanel = Frozen("#181818");

    // Акцент — мягкий белый свет; вторичный акцент — светло-серый
    public static readonly Brush Accent = Frozen("#FFFFFF");
    public static readonly Brush AccentHover = Frozen("#D6D6D6");
    public static readonly Brush AccentSoft = Frozen("#4A4A4A");
    public static readonly Brush Secondary = Frozen("#C8C8C8");
    public static readonly Brush SecondarySoft = Frozen("#3A3A3A");

    // Статусы остаются монохромными и различаются яркостью
    public static readonly Brush Success = Frozen("#E6E6E6");
    public static readonly Brush Danger = Frozen("#FFFFFF");

    // Текст — светлый, но не ослепительно белый
    public static readonly Brush TextPrimary = Frozen("#F2F2F2");
    public static readonly Brush TextSecondary = Frozen("#A6A6A6");
    public static readonly Brush TextMuted = Frozen("#666666");

    // Стекло: полупрозрачные слои и тонкие границы
    public static readonly Brush Glass = Frozen("#0AFFFFFF");
    public static readonly Brush GlassHover = Frozen("#26FFFFFF");
    public static readonly Brush GlassBorder = Frozen("#1AFFFFFF");
    public static readonly Brush GlassBorderBright = Frozen("#3AFFFFFF");
    public static readonly Brush CardSurface = Frozen("#C40C0C0C");
    public static readonly Brush CardSurfaceHover = Frozen("#D8141414");
    public static readonly Brush CardBorderHover = Frozen("#99FFFFFF");

    /// <summary>
    /// Граница карточки в покое. Заметно ярче GlassBorder (10%): тонкая линия
    /// на полупрозрачной поверхности, под которой движется космический фон,
    /// иначе «смывается» и выглядит прерывистой.
    /// </summary>
    public static readonly Brush CardBorderRest = Frozen("#3DFFFFFF");

    public static readonly Brush CoverPlaceholder = Frozen("#101010");
    public static readonly Brush OrbitLine = Frozen("#14FFFFFF");

    // Градиент главных действий: белый → светло-серый, остаётся монохромным
    public static LinearGradientBrush AccentGradient()
    {
        var from = (Color)ColorConverter.ConvertFromString("#FFFFFF");
        var to = (Color)ColorConverter.ConvertFromString("#CFCFCF");
        return new LinearGradientBrush(from, to, 135);
    }

    /// <summary>Мягкое объёмное свечение выбранного цвета.</summary>
    public static DropShadowEffect Glow(Color color, double opacity = 0.30, double blur = 26) => new()
    {
        Color = color,
        BlurRadius = blur,
        ShadowDepth = 0,
        Opacity = opacity
    };

    /// <summary>Белое свечение — основной акцент монохромной темы.</summary>
    public static DropShadowEffect Glow(double opacity = 0.30, double blur = 26) =>
        Glow(Colors.White, opacity, blur);

    /// <summary>Объёмная тень карточки: глубокая, направленная вниз.</summary>
    public static DropShadowEffect CardShadowEffect() => new()
    {
        Color = Colors.Black,
        BlurRadius = 24,
        ShadowDepth = 8,
        Direction = 270,
        Opacity = 0.55
    };

    /// <summary>Размытие для фоновых туманностей.</summary>
    public static BlurEffect Nebula(double radius = 90) => new() { Radius = radius };

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
