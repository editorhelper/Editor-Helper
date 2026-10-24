using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace EditorHelper.Setup.Ui;

/// <summary>
/// Единая система анимаций: согласованные длительности, кривые ускорения
/// и готовые переходы для появления, наведения, нажатия и уведомления.
/// Все анимации уважают флаг EffectsEnabled (настройки + системное «уменьшение движения»).
/// </summary>
public static class Motion
{
    // Длительности (мс) — одна сетка на всё приложение
    public const double Fast = 120;
    public const double Base = 220;
    public const double Slow = 380;
    public const double Page = 300;

    public static bool Enabled { get; set; } = true;

    public static IEasingFunction EaseOut { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static IEasingFunction EaseInOut { get; } = new SineEase { EasingMode = EasingMode.EaseInOut };
    public static IEasingFunction Snap { get; } = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

    // ---------------------------------------------------------------- Появление

    /// <summary>Элемент плавно проявляется и всплывает снизу с задержкой (каскад).</summary>
    public static void Entrance(FrameworkElement element, double delaySeconds = 0, double distance = 16)
    {
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }

        element.Opacity = 0;
        var translate = FindOrCreateTranslate(element);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.Y = distance;

        var delay = TimeSpan.FromSeconds(delaySeconds);

        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
            distance, 0, TimeSpan.FromMilliseconds(Slow))
        {
            BeginTime = delay,
            EasingFunction = EaseOut
        });

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
            0, 1, TimeSpan.FromMilliseconds(Base + 60))
        {
            BeginTime = delay,
            EasingFunction = EaseOut
        });
    }

    /// <summary>
    /// Находит TranslateTransform в существующем RenderTransform или создаёт его.
    /// Без этого Entrance затирал бы трансформ, установленный AttachCardInteraction,
    /// и hover-анимации карточек переставали работать.
    /// </summary>
    internal static TranslateTransform FindOrCreateTranslate(FrameworkElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform existing:
                return existing;
            case TransformGroup group:
                foreach (var child in group.Children)
                    if (child is TranslateTransform found)
                        return found;
                var added = new TranslateTransform();
                group.Children.Add(added);
                return added;
            case null:
            case Transform identity when identity == Transform.Identity:
                var fresh = new TranslateTransform();
                element.RenderTransform = fresh;
                return fresh;
            default:
                var wrapper = new TransformGroup();
                wrapper.Children.Add(element.RenderTransform);
                var translate = new TranslateTransform();
                wrapper.Children.Add(translate);
                element.RenderTransform = wrapper;
                return translate;
        }
    }

    /// <summary>Каскад детей: каждый следующий проявляется со сдвигом step.</summary>
    public static void EntranceCascade(System.Collections.IEnumerable elements, double step = 0.05, double distance = 16)
    {
        int i = 0;
        foreach (FrameworkElement element in elements)
        {
            Entrance(element, Math.Min(i, 14) * step, distance);
            i++;
        }
    }

    /// <summary>
    /// Переход страницы: старое содержимое гаснет и сдвигается вверх,
    /// новое всплывает снизу. direction: +1 вперёд по навигации, -1 назад.
    /// </summary>
    public static void PageTransition(FrameworkElement container, Action rebuild, double direction = 1)
    {
        if (!Enabled)
        {
            rebuild();
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Fast)) { EasingFunction = EaseOut };
        fadeOut.Completed += (_, _) =>
        {
            container.BeginAnimation(UIElement.OpacityProperty, null);
            container.Opacity = 1;
            rebuild();

            var slide = new ThicknessAnimation(
                new Thickness(0, 12 * direction, 0, 0), new Thickness(0),
                TimeSpan.FromMilliseconds(Page)) { EasingFunction = EaseOut };
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Page)) { EasingFunction = EaseOut };

            container.BeginAnimation(FrameworkElement.MarginProperty, slide);
            container.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };

        container.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    // ---------------------------------------------------------------- Наведение / нажатие

    /// <summary>
    /// Hover-поведение карточки: плавное проявление обводки по цвету, мягкое свечение,
    /// лёгкий подъём и подсветка поверхности; нажатие — короткий отклик вниз.
    ///
    /// glowLayer — необязательный отдельный элемент СЗАДИ карточки, на котором живёт
    /// свечение. DropShadowEffect нельзя вешать на саму карточку: эффект переводит
    /// её поддерево (текст и границу 1px) в растровый кэш, из-за чего граница
    /// размывается и «гуляет» по толщине, а текст мылится. Отдельный соседний слой
    /// решает это: свечение не обрезается ничем, потому что лежит в том же Grid,
    /// что и карточка, и не касается её визуального дерева.
    ///
    /// Сама карточка сознательно не масштабируется: при scale её верхний край
    /// уезжает на дробную позицию, горизонтальная линия 1px растекается
    /// антиалиасингом по двум строкам и вдвое тускнеет — так «пропадала» верхняя
    /// обводка. Увеличивается только размытый слой свечения, которому дробные
    /// смещения краёв безвредны. Отклик при наведении дают подъём на целые
    /// пиксели, свечение и подсветка поверхности.
    ///
    /// Обводка анимируется по цвету на своей кисти (не замороженной, своя на
    /// экземпляр), поэтому появляется и гаснет плавно, без скачка.
    /// </summary>
    public static void AttachCardInteraction(Border card, Brush hoverBorder, Brush? restBorder = null,
        FrameworkElement? glowLayer = null, FrameworkElement? transformTarget = null)
    {
        restBorder ??= card.BorderBrush;

        var restColor = ColorOf(restBorder);
        var hoverColor = ColorOf(hoverBorder);

        // Своя кисть на карточку: её Color можно анимировать (замороженную — нельзя)
        var borderBrush = new SolidColorBrush(restColor);
        card.BorderBrush = borderBrush;

        // Отдельная кисть фона: подсвечиваем саму поверхность.
        // Анимируем фон только если это SolidColorBrush — градиенты и т.п. не трогаем.
        SolidColorBrush? fillBrush = null;
        Color restFill = default;
        Color hoverFill = default;
        if (card.Background is SolidColorBrush solidFill)
        {
            restFill = solidFill.Color;
            hoverFill = WithAlphaBump(restFill);
            fillBrush = new SolidColorBrush(restFill);
            card.Background = fillBrush;
        }

        if (glowLayer != null)
        {
            glowLayer.Opacity = 0;
            glowLayer.IsHitTestVisible = false;
        }

        // Двигаем общий контейнер (если передан), чтобы карточка и её ореол
        // смещались синхронно и свечение не «отставало» от карточки.
        var moveTarget = transformTarget ?? (FrameworkElement)card;
        var translate = FindOrCreateTranslate(moveTarget);

        // Масштабируем ТОЛЬКО слой свечения: он размытый, поэтому дробное
        // смещение краёв ему безвредно. Саму карточку с чёткой границей 1px
        // масштабировать нельзя — её верхний край уехал бы на дробную позицию,
        // горизонтальная линия растеклась бы антиалиасингом по двум строкам
        // и вдвое потускнела. Именно так «пропадала» верхняя обводка.
        ScaleTransform? glowScale = null;
        if (glowLayer != null)
        {
            glowScale = new ScaleTransform(1, 1);
            glowLayer.RenderTransform = glowScale;
            glowLayer.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        card.MouseEnter += (_, _) =>
        {
            if (!Enabled)
            {
                // Без анимаций состояние всё равно должно быть полным и мгновенным
                borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                borderBrush.Color = hoverColor;
                if (fillBrush != null)
                {
                    fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    fillBrush.Color = hoverFill;
                }
                if (glowLayer != null)
                {
                    glowLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    glowLayer.Opacity = 1;
                }
                return;
            }

            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(Base)) { EasingFunction = EaseOut });
            fillBrush?.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(hoverFill, TimeSpan.FromMilliseconds(Base)) { EasingFunction = EaseOut });

            glowLayer?.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(Slow)) { EasingFunction = EaseOut });
            if (glowScale != null)
            {
                glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, Grow(HoverScale));
                glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, Grow(HoverScale));
            }

            translate.BeginAnimation(TranslateTransform.YProperty, Lift(-3));
        };

        card.MouseLeave += (_, _) =>
        {
            if (!Enabled)
            {
                borderBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                borderBrush.Color = restColor;
                if (fillBrush != null)
                {
                    fillBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                    fillBrush.Color = restFill;
                }
                if (glowLayer != null)
                {
                    glowLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    glowLayer.Opacity = 0;
                }
                return;
            }

            borderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(restColor, TimeSpan.FromMilliseconds(Slow)) { EasingFunction = EaseOut });
            fillBrush?.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(restFill, TimeSpan.FromMilliseconds(Slow)) { EasingFunction = EaseOut });

            glowLayer?.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(Slow + 120)) { EasingFunction = EaseOut });
            if (glowScale != null)
            {
                glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, Grow(1.0, Slow));
                glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, Grow(1.0, Slow));
            }

            translate.BeginAnimation(TranslateTransform.YProperty, Lift(0));
        };

        card.MouseLeftButtonDown += (_, _) =>
        {
            if (!Enabled) return;
            // Нажатие — короткий отклик вниз. Масштаб не применяем: он сдвинул бы
            // верхнюю границу карточки на дробную позицию и размыл бы её.
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)) { EasingFunction = EaseOut });
        };

        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!Enabled) return;
            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-3, TimeSpan.FromMilliseconds(Fast)) { EasingFunction = Snap });
        };
    }

    /// <summary>Прирост ореола при наведении — сдержанный, чтобы не «выпрыгивать».</summary>
    private const double HoverScale = 1.015;

    private static DoubleAnimation Grow(double to, double ms = Base) =>
        new(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };

    private static Color ColorOf(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        _ => Colors.White
    };

    /// <summary>
    /// Чуть осветляет поверхность при наведении — заменяет убранную тень как
    /// тактильный отклик. Альфа сохраняется, RGB подтягиваются к белому.
    /// </summary>
    private static Color WithAlphaBump(Color c) =>
        Color.FromArgb(c.A, Bump(c.R), Bump(c.G), Bump(c.B));

    private static byte Bump(byte v) => (byte)Math.Min(255, v + 20);

    private static DoubleAnimation Lift(double to) => new(to, TimeSpan.FromMilliseconds(Base)) { EasingFunction = EaseOut };

    /// <summary>Однократный «пульс» — подтверждение действия (избранное, копирование).</summary>
    public static void Pulse(FrameworkElement element, double scaleTo = 1.25)
    {
        if (!Enabled) return;

        var transform = element.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        element.RenderTransform = transform;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, scaleTo, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = EaseInOut
        });
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, scaleTo, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = EaseInOut
        });
    }

    // ---------------------------------------------------------------- Плавное значение

    /// <summary>Плавно анимирует произвольное double-свойство элемента (индикатор навигации, ширина поиска).</summary>
    public static void Animate(FrameworkElement target, DependencyProperty property, double to,
        double milliseconds = Base, IEasingFunction? easing = null)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing ?? EaseOut
        });
    }
}
