using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EditorHelper.Ui;

/// <summary>
/// Аватар приложения с мягко растворяющимися краями.
///
/// Обычная эллиптическая маска даёт жёсткий круглый контур — на космическом фоне
/// он читается как вырезанная картинка. Здесь вместо обрезки используется
/// OpacityMask с радиальным градиентом альфа-канала: центр остаётся полностью
/// непрозрачным (и потому максимально чётким), а к краям альфа плавно уходит в ноль.
///
/// Важные свойства такого решения:
///  — изображение не размывается, меняется только его прозрачность, поэтому цвета
///    и пропорции сохраняются полностью;
///  — маска задаётся в относительных координатах, поэтому работает одинаково
///    на любом размере (32 px в верхней панели и ~100 px в hero-композиции);
///  — за последним стопом альфа равна нулю, поэтому углы исходного квадратного
///    PNG становятся прозрачными сами — отдельная обрезка по кругу не нужна;
///  — эффект строго симметричен относительно центра.
///
/// Класс предназначен только для аватара. Прочие изображения приложения
/// (обложки ресурсов, превью) не трогаются.
/// </summary>
public sealed class AvatarImage : Image
{
    /// <summary>
    /// Доля радиуса, до которой изображение остаётся полностью непрозрачным.
    /// Чем больше значение, тем уже зона растворения и тем «резче» край;
    /// чем меньше — тем мягче переход, но сильнее теряется площадь картинки.
    /// </summary>
    private const double SharpCore = 0.60;

    private static readonly Brush SoftEdgeMask = BuildMask();

    public AvatarImage()
    {
        Stretch = Stretch.UniformToFill;
        // Аватар — элемент оформления: клики должны проходить к родительской кнопке
        IsHitTestVisible = false;
        OpacityMask = SoftEdgeMask;
    }

    /// <summary>
    /// Радиальная маска прозрачности: ядро без потерь, затем плавный спад альфы.
    /// Промежуточные стопы подбирались так, чтобы край не выглядел ни обрезанным,
    /// ни «выцветшим» — спад начинается поздно и заканчивается точно на границе.
    /// </summary>
    private static Brush BuildMask()
    {
        var mask = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };

        // Цвет не важен — OpacityMask учитывает только альфа-канал
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), 0.00));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0, 0, 0), SharpCore));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(238, 0, 0, 0), SharpCore + 0.08));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(186, 0, 0, 0), SharpCore + 0.16));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(110, 0, 0, 0), SharpCore + 0.24));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(44, 0, 0, 0), SharpCore + 0.32));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1.00));

        mask.Freeze();
        return mask;
    }
}
