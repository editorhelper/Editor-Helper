using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EditorHelper.Models;

namespace EditorHelper.Ui;

/// <summary>
/// Карточка ресурса в каталоге. Один общий построитель: им пользуется сетка
/// разделов и предпросмотр карточки в админ-панели, чтобы владелец видел ровно
/// то оформление, которое получат пользователи.
///
/// Hover — как у остальных карточек приложения: мягкое осветление границы и
/// фона, подъём на 3px (Motion.AttachCardInteraction). Свечения намеренно нет:
/// DropShadow-ореол выходил за зазор между карточками и ложился поверх соседей.
/// </summary>
public static class ResourceCardView
{
    /// <param name="coverImage">
    /// Готовая обложка вместо загрузки по <see cref="Resource.Thumbnail"/>.
    /// Нужна предпросмотру в админке: там изображение ещё не опубликовано и
    /// отдаётся только по сессии владельца, а не по открытому URL.
    /// </param>
    public static Border Create(
        Resource resource, string? downloadedPath,
        Action? onClick = null, ImageSource? coverImage = null)
    {
        var card = new Border
        {
            Background = Palette.CardSurface,
            BorderBrush = Palette.CardBorderRest,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };

        var stack = new StackPanel();
        stack.Children.Add(CreateCover(resource, coverImage));

        var content = new StackPanel { Margin = new Thickness(18) };

        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        badgeRow.Children.Add(CreateBadge(resource.Type.ToString()));
        if (downloadedPath != null)
            badgeRow.Children.Add(CreateBadge("Downloaded", Palette.Success));
        content.Children.Add(badgeRow);

        content.Children.Add(new TextBlock
        {
            Text = resource.Title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        content.Children.Add(new TextBlock
        {
            Text = resource.Description,
            FontSize = 12.5,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Height = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var appText = new TextBlock
        {
            Text = resource.Application,
            FontSize = 11.5,
            Foreground = Palette.TextMuted
        };
        Grid.SetColumn(appText, 0);

        var versionText = new TextBlock
        {
            Text = $"v{resource.Version}",
            FontSize = 11.5,
            Foreground = Palette.TextMuted
        };
        Grid.SetColumn(versionText, 1);

        footer.Children.Add(appText);
        footer.Children.Add(versionText);
        content.Children.Add(footer);

        stack.Children.Add(content);
        card.Child = stack;
        card.Margin = new Thickness(0, 0, 20, 20);

        // Граница при наведении — мягче CardBorderHover (60% белого): на тёмной
        // сетке яркий контур спорил со свечением соседних карточек и «кричал».
        Motion.AttachCardInteraction(card, Palette.GlassBorderBright, Palette.CardBorderRest);

        if (onClick != null)
        {
            var pressOrigin = new Point();
            card.PreviewMouseLeftButtonDown += (_, e) => pressOrigin = e.GetPosition(card);
            card.MouseLeftButtonUp += (_, e) =>
            {
                // Клик, а не drag: открытие окна после «протаскивания» курсора
                // по карточке выглядело как ложное срабатывание
                var pos = e.GetPosition(card);
                if (Math.Abs(pos.X - pressOrigin.X) < 6 && Math.Abs(pos.Y - pressOrigin.Y) < 6)
                    onClick();
            };
        }

        return card;
    }

    /// <summary>Обложка: изображение, если оно есть, иначе типографическая плашка с орбитой.</summary>
    private static Border CreateCover(Resource resource, ImageSource? provided)
    {
        var cover = new Border
        {
            Background = Palette.CoverPlaceholder,
            Height = 150,
            CornerRadius = new CornerRadius(13, 13, 0, 0),
            ClipToBounds = true
        };

        var image = provided ?? ThumbnailLoader.TryLoad(resource.Thumbnail);
        if (image != null)
        {
            cover.Child = new Image
            {
                Source = image,
                Stretch = Stretch.UniformToFill
            };
            return cover;
        }

        var placeholder = new Grid();

        // Лёгкий градиент глубины на обложке-заглушке
        placeholder.Background = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString("#161616"),
            (Color)ColorConverter.ConvertFromString("#0A0A0A"), 90);

        placeholder.Children.Add(new Ellipse
        {
            Width = 96,
            Height = 96,
            Stroke = Palette.CardBorderHover,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5
        });
        placeholder.Children.Add(new Ellipse
        {
            Width = 62,
            Height = 62,
            Stroke = Palette.GlassBorder,
            StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        placeholder.Children.Add(new TextBlock
        {
            Text = resource.Type.ToString().ToUpperInvariant(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        cover.Child = placeholder;
        return cover;
    }

    private static Border CreateBadge(string text, Brush? foreground = null)
    {
        var badge = new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 8, 0)
        };

        badge.Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = foreground ?? Palette.TextSecondary
        };

        return badge;
    }
}
