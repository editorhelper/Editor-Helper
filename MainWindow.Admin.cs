using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using EditorHelper.Services;
using EditorHelper.Ui;
using EditorHelper.Views;

namespace EditorHelper;

/// <summary>
/// Раздел Admin Panel: список карточек каталога и действия над ними.
///
/// Панель — это только удобство. Каждое действие уходит на сервер, который
/// проверяет роль Owner по сессии: скрытый пункт меню и недоступный раздел
/// защитой не являются и не должны ею считаться. Обычный пользователь,
/// обратившись к тем же маршрутам напрямую, получит 401/403.
/// </summary>
public partial class MainWindow
{
    private const int AdminPageSize = 12;

    private static readonly (string Label, string Value)[] AdminTypeFilters =
    {
        ("Все типы", ""), ("Script", "Script"), ("Plugin", "Plugin"), ("Preset", "Preset"),
        ("Overlay", "Overlay"), ("Sound", "Sound"), ("Texture", "Texture"),
        ("Template", "Template"), ("Extension", "Extension"), ("Other", "Other")
    };

    private static readonly (string Label, string Value)[] AdminStatusFilters =
    {
        ("Все статусы", ""), ("Опубликован", "published"), ("Черновик", "draft"), ("Скрыт", "hidden")
    };

    private static readonly (string Label, string Value)[] AdminSorts =
    {
        ("Сначала изменённые", "updated"), ("Сначала опубликованные", "published"),
        ("По названию", "title"), ("По типу", "type")
    };

    private List<AdminResource> _adminResources = new();
    private string _adminQuery = "";
    private string _adminTypeFilter = "";
    private string _adminStatusFilter = "";
    private string _adminSort = "updated";
    private int _adminPage;
    private bool _adminBusy;
    private bool _adminReloadQueued;

    private StackPanel? _adminListHost;
    private TextBlock? _adminSummary;
    private StackPanel? _adminPager;

    private async void LoadAdminPage()
    {
        // Запрос, отложенный предыдущим сеансом в разделе, уже не нужен:
        // ниже стартует новая загрузка, которая отдаст актуальные данные.
        _adminReloadQueued = false;

        AddPageHeader("Admin Panel", "Карточки каталога: создание, публикация и удаление");

        if (_adminService == null)
        {
            ContentArea.Children.Add(CreateErrorState(
                "Раздел недоступен",
                "Админ-панель работает только при входе под ролью Owner и только с удалённым сервером.",
                "Войдите в аккаунт владельца и выберите Remote Server в настройках."));
            return;
        }

        ContentArea.Children.Add(CreateAdminToolbar());

        _adminSummary = new TextBlock
        {
            FontSize = 12,
            Foreground = Palette.TextMuted,
            Margin = new Thickness(0, 0, 0, 14)
        };
        ContentArea.Children.Add(_adminSummary);

        _adminListHost = new StackPanel();
        ContentArea.Children.Add(_adminListHost);

        _adminPager = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 24)
        };
        ContentArea.Children.Add(_adminPager);

        await ReloadAdminResourcesAsync();
    }

    private Border CreateAdminToolbar()
    {
        var toolbar = CreateGlassCard();
        toolbar.Padding = new Thickness(16, 14, 16, 14);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 5; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchFrame = new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        var searchGrid = new Grid();
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchGrid.Children.Add(new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Palette.TextMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        });

        var searchBox = new TextBox
        {
            Text = _adminQuery,
            Style = (Style)FindResource("GlassTextBox"),
            Padding = new Thickness(10, 9, 12, 9),
            ToolTip = "Поиск по названию, описанию, категории, автору и тегам"
        };
        searchBox.TextChanged += (_, _) =>
        {
            _adminQuery = searchBox.Text;
            _adminPage = 0;
            RenderAdminList();
        };
        Grid.SetColumn(searchBox, 1);
        searchGrid.Children.Add(searchBox);
        searchFrame.Child = searchGrid;
        grid.Children.Add(searchFrame);

        grid.Children.Add(AdminFilterCombo(1, AdminTypeFilters, _adminTypeFilter, 140, value =>
        {
            _adminTypeFilter = value;
            _adminPage = 0;
            RenderAdminList();
        }));

        grid.Children.Add(AdminFilterCombo(2, AdminStatusFilters, _adminStatusFilter, 150, value =>
        {
            _adminStatusFilter = value;
            _adminPage = 0;
            RenderAdminList();
        }));

        grid.Children.Add(AdminFilterCombo(3, AdminSorts, _adminSort, 190, value =>
        {
            _adminSort = value;
            _adminPage = 0;
            RenderAdminList();
        }));

        var refresh = new Button
        {
            Content = "\uE72C",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = "Обновить список с сервера"
        };
        refresh.Click += async (_, _) => await ReloadAdminResourcesAsync();
        Grid.SetColumn(refresh, 4);
        grid.Children.Add(refresh);

        var create = new Button
        {
            Content = "Новая карточка",
            Style = (Style)FindResource("PrimaryButton"),
            Padding = new Thickness(18, 9, 18, 9),
            Margin = new Thickness(10, 0, 0, 0)
        };
        create.Click += (_, _) => OpenAdminEditor(null);
        Grid.SetColumn(create, 5);
        grid.Children.Add(create);

        toolbar.Child = grid;
        Motion.Entrance(toolbar, 0.02);
        return toolbar;
    }

    private ComboBox AdminFilterCombo(
        int column, (string Label, string Value)[] options, string selected,
        double width, Action<string> onChange)
    {
        var combo = new ComboBox
        {
            Style = (Style)FindResource("GlassComboBox"),
            Width = width,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var option in options)
            combo.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });

        var index = Array.FindIndex(options, o => o.Value == selected);
        combo.SelectedIndex = index < 0 ? 0 : index;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value)
                onChange(value);
        };

        Grid.SetColumn(combo, column);
        return combo;
    }

    private async Task ReloadAdminResourcesAsync()
    {
        if (_adminService == null) return;

        // Загрузка уже идёт — не бросаем запрос, а просим повторить после неё.
        // Раньше повторный вызов просто выходил, и если первый загрузка
        // стартовала до перестроения страницы, список оставался на спиннере.
        if (_adminBusy) { _adminReloadQueued = true; return; }

        var page = _activePage;
        _adminBusy = true;

        if (_adminListHost != null)
        {
            _adminListHost.Children.Clear();
            _adminListHost.Children.Add(CreateLoadingState());
        }
        if (_adminPager != null) _adminPager.Children.Clear();
        if (_adminSummary != null) _adminSummary.Text = "";

        // Страница могла смениться, пока шёл запрос: тогда обновляется только
        // кэш, а отрисовку сделает активная страница.
        try
        {
            var resources = await _adminService.ListAsync();
            _adminResources = resources;
            if (page == _activePage && _adminListHost != null)
            {
                _adminPage = Math.Min(_adminPage, Math.Max(0, (resources.Count - 1) / AdminPageSize));
                RenderAdminList();
                StatusText.Text = $"Admin: {resources.Count} карточек";
            }
        }
        catch (Exception ex)
        {
            if (page == _activePage && _adminListHost != null)
            {
                _adminListHost!.Children.Clear();
                _adminListHost.Children.Add(CreateErrorState(
                    "Не удалось загрузить карточки",
                    ex.Message,
                    ex is AdminApiException { StatusCode: 401 or 403 }
                        ? "Сессия потеряла права владельца — войдите заново под аккаунтом Owner."
                        : null));
            }
        }
        finally
        {
            _adminBusy = false;
        }

        // Отложенный запрос повторяем, только если раздел всё ещё открыт
        if (_adminReloadQueued && page == _activePage)
        {
            _adminReloadQueued = false;
            await ReloadAdminResourcesAsync();
        }
    }

    private void RenderAdminList()
    {
        if (_adminListHost == null) return;

        var filtered = FilteredAdminResources();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)AdminPageSize));
        _adminPage = Math.Clamp(_adminPage, 0, pages - 1);
        var slice = filtered.Skip(_adminPage * AdminPageSize).Take(AdminPageSize).ToList();

        _adminListHost.Children.Clear();

        if (_adminSummary != null)
        {
            var published = _adminResources.Count(r => r.Status == "published");
            var draft = _adminResources.Count(r => r.Status == "draft");
            var hidden = _adminResources.Count(r => r.Status == "hidden");

            _adminSummary.Text = _adminResources.Count == 0
                ? "В каталоге пока нет карточек — создайте первую."
                : $"Всего {_adminResources.Count} · опубликовано {published} · черновиков {draft} · скрыто {hidden}" +
                  (filtered.Count != _adminResources.Count ? $" · в выборке {filtered.Count}" : "");
        }

        if (slice.Count == 0)
        {
            _adminListHost.Children.Add(CreateEmptyState(
                "Ничего не найдено",
                "Под текущий поиск и фильтры не подходит ни одна карточка."));
        }
        else
        {
            int index = 0;
            foreach (var resource in slice)
            {
                var row = CreateAdminRow(resource);
                _adminListHost.Children.Add(row);
                Motion.Entrance(row, Math.Min(index, 10) * 0.035, 10);
                index++;
            }
        }

        RenderAdminPager(pages);
    }

    private List<AdminResource> FilteredAdminResources()
    {
        IEnumerable<AdminResource> query = _adminResources;

        if (!string.IsNullOrWhiteSpace(_adminQuery))
        {
            var q = _adminQuery.Trim();
            query = query.Where(r =>
                Contains(r.Title, q) || Contains(r.Description, q) || Contains(r.LongDescription, q) ||
                Contains(r.Category, q) || Contains(r.Author, q) || Contains(r.Slug, q) ||
                r.Tags.Any(t => Contains(t, q)));
        }

        if (!string.IsNullOrEmpty(_adminTypeFilter))
            query = query.Where(r => string.Equals(r.Type, _adminTypeFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(_adminStatusFilter))
            query = query.Where(r => string.Equals(r.Status, _adminStatusFilter, StringComparison.OrdinalIgnoreCase));

        return _adminSort switch
        {
            "title" => query.OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "type" => query
                .OrderBy(r => r.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList(),
            "published" => query.OrderByDescending(r => r.PublishedAt).ToList(),
            _ => query.OrderByDescending(r => r.UpdatedAt).ToList()
        };
    }

    private static bool Contains(string? value, string query) =>
        value != null && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private void RenderAdminPager(int pages)
    {
        if (_adminPager == null) return;
        _adminPager.Children.Clear();
        if (pages <= 1) return;

        _adminPager.Children.Add(AdminPagerButton("\uE76B", "Предыдущая страница",
            _adminPage > 0, () => { _adminPage--; RenderAdminList(); ScrollAdminToTop(); }));

        _adminPager.Children.Add(new TextBlock
        {
            Text = $"Страница {_adminPage + 1} из {pages}",
            FontSize = 12,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0)
        });

        _adminPager.Children.Add(AdminPagerButton("\uE76C", "Следующая страница",
            _adminPage < pages - 1, () => { _adminPage++; RenderAdminList(); ScrollAdminToTop(); }));
    }

    private Button AdminPagerButton(string glyph, string tooltip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(12, 7, 12, 7),
            IsEnabled = enabled,
            ToolTip = tooltip
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ScrollAdminToTop() => ContentScroll.ScrollToTop();

    // ---------------------------------------------------------------- Строка карточки

    private Border CreateAdminRow(AdminResource resource)
    {
        var row = new Border
        {
            Background = Palette.CardSurface,
            BorderBrush = Palette.CardBorderRest,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(AdminStatusChip(resource));

        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 14, 0)
        };

        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(resource.Title) ? "(без названия)" : resource.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(resource.Description) ? "Без описания" : resource.Description,
            FontSize = 12,
            Foreground = Palette.TextSecondary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left
        });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        meta.Children.Add(CreateBadge(resource.Type));
        meta.Children.Add(CreateBadge(resource.Application));
        meta.Children.Add(CreateBadge($"v{resource.Version}"));
        if (!string.IsNullOrWhiteSpace(resource.Category)) meta.Children.Add(CreateBadge(resource.Category!));
        meta.Children.Add(CreateBadge(DescribeSource(resource.Source)));
        if (resource.IsFeatured) meta.Children.Add(CreateBadge("В избранном", Palette.Accent));
        info.Children.Add(meta);

        info.Children.Add(new TextBlock
        {
            Text = $"Изменено {Format.Date(resource.UpdatedAt.ToLocalTime())}" +
                   (resource.PublishedAt == default ? "" : $" · опубликовано {Format.Date(resource.PublishedAt.ToLocalTime())}"),
            FontSize = 11,
            Foreground = Palette.TextMuted,
            Margin = new Thickness(0, 8, 0, 0)
        });

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        actions.Children.Add(AdminIconButton("\uE70F", "Редактировать", () => OpenAdminEditor(resource)));
        actions.Children.Add(AdminIconButton("\uE8C8", "Дублировать", () => _ = DuplicateResourceAsync(resource)));
        actions.Children.Add(AdminStatusMenuButton(resource));
        actions.Children.Add(AdminIconButton("\uE74D", "Удалить", () => _ = DeleteResourceAsync(resource)));

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        row.Child = grid;
        return row;
    }

    /// <summary>
    /// Индикатор статуса. Тема монохромная, поэтому статусы различаются
    /// яркостью и подписью, а не цветом: опубликованная карточка — самая
    /// светлая, черновик — приглушённый, скрытая — с перечёркнутой рамкой.
    /// </summary>
    private Border AdminStatusChip(AdminResource resource)
    {
        var (label, foreground, border, fill) = resource.Status switch
        {
            "published" => ("Опубликован", Palette.TextPrimary, Palette.CardBorderHover, Palette.GlassHover),
            "hidden" => ("Скрыт", Palette.TextSecondary, Palette.GlassBorderBright, Palette.Glass),
            _ => ("Черновик", Palette.TextMuted, Palette.GlassBorder, Brushes.Transparent)
        };

        var chip = new Border
        {
            Background = fill,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 7, 11, 7),
            Width = 116,
            VerticalAlignment = VerticalAlignment.Center
        };

        chip.Child = new TextBlock
        {
            Text = label,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        return chip;
    }

    private Button AdminIconButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = tooltip
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Popup? _adminStatusMenu;

    /// <summary>
    /// Кнопка смены статуса: одна компактная кнопка вместо трёх, потому что
    /// одновременны показаны быть не должны — публикация, возврат в черновик
    /// и скрытие выбираются из короткого меню.
    /// </summary>
    private Button AdminStatusMenuButton(AdminResource resource)
    {
        var button = new Button
        {
            Content = "\uE712",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Изменить статус"
        };

        button.Click += (_, _) =>
        {
            if (_adminStatusMenu?.IsOpen == true) { _adminStatusMenu.IsOpen = false; return; }

            var card = new Border
            {
                Background = Palette.BgElevated,
                BorderBrush = Palette.GlassBorderBright,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8),
                MinWidth = 240,
                Effect = Palette.CardShadowEffect()
            };

            var root = new StackPanel();

            if (resource.Status != "published")
                root.Children.Add(CreateAccountMenuItem("\uE895", "Опубликовать в каталоге",
                    () => CloseStatusMenu(() => _ = ChangeStatusAsync(resource, "publish"))));

            if (resource.Status != "draft")
                root.Children.Add(CreateAccountMenuItem("\uE8A7",
                    resource.Status == "published" ? "Снять с публикации" : "Вернуть в черновики",
                    () => CloseStatusMenu(() => _ = ChangeStatusAsync(resource, "unpublish"))));

            if (resource.Status != "hidden")
                root.Children.Add(CreateAccountMenuItem("\uED1A", "Скрыть из каталога",
                    () => CloseStatusMenu(() => _ = ChangeStatusAsync(resource, "hide"))));

            card.Child = root;

            _adminStatusMenu = new Popup
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 6,
                HorizontalOffset = -60,
                StaysOpen = false,
                AllowsTransparency = true,
                Child = card
            };
            _adminStatusMenu.Opened += (_, _) => Motion.Entrance(card, 0, 8);
            _adminStatusMenu.IsOpen = true;
        };

        return button;
    }

    private void CloseStatusMenu(Action next)
    {
        if (_adminStatusMenu != null) _adminStatusMenu.IsOpen = false;
        next();
    }

    private static string DescribeSource(AdminFileSource? source) => source?.Kind.ToLowerInvariant() switch
    {
        "telegram" => "Telegram",
        "http" => "Внешняя ссылка",
        "local" => string.IsNullOrWhiteSpace(source.LocalPath) ? "Без файла" : "Файл на сервере",
        _ => "Без файла"
    };

    // ---------------------------------------------------------------- Действия

    private async Task RunAdminActionAsync(Func<AdminService, Task> action, string successMessage)
    {
        if (_adminService == null) return;

        try
        {
            await action(_adminService);
            ShowToast(successMessage, Palette.Accent);
            await ReloadAdminResourcesAsync();
        }
        catch (AdminApiException ex)
        {
            ShowToast(ex.Message, Palette.Secondary);
        }
        catch (Exception ex)
        {
            ShowToast("Сервер недоступен: " + ex.Message, Palette.Secondary);
        }
    }

    private Task ChangeStatusAsync(AdminResource resource, string action)
    {
        var label = action switch
        {
            "publish" => "Опубликовано: «" + resource.Title + "»",
            "unpublish" => "Снято с публикации: «" + resource.Title + "»",
            "hide" => "Скрыто из каталога: «" + resource.Title + "»",
            _ => "Статус обновлён"
        };

        return RunAdminActionAsync(
            service => service.SetStatusAsync(resource.Id, action),
            label);
    }

    private Task DuplicateResourceAsync(AdminResource resource) =>
        RunAdminActionAsync(
            service => service.DuplicateAsync(resource.Id),
            $"Создан черновик-копия «{resource.Title}»");

    private async Task DeleteResourceAsync(AdminResource resource)
    {
        var dialog = new ConfirmDialog(
            "Удалить карточку?",
            $"«{resource.Title}» будет удалена с сервера вместе со всеми полями.",
            resource.Status == "published"
                ? "Карточка опубликована: она сразу исчезнет из каталога у всех пользователей."
                : null,
            "Удалить")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return;

        await RunAdminActionAsync(
            service => service.DeleteAsync(resource.Id),
            $"Карточка «{resource.Title}» удалена");
    }

    /// <summary>
    /// Открывает форму карточки внутри главного окна: список скрывается,
    /// форма занимает его место. Отдельное окно не используется — панель
    /// должна оставаться частью приложения и его навигации.
    /// </summary>
    private void OpenAdminEditor(AdminResource? resource)
    {
        if (_adminService == null) return;

        var editor = new AdminEditorView(_adminService, resource, message => CloseAdminEditor(message));

        AdminEditorHost.Children.Clear();
        AdminEditorHost.Children.Add(editor);
        ContentScroll.Visibility = Visibility.Collapsed;
        AdminEditorHost.Visibility = Visibility.Visible;
        ContentScroll.ScrollToTop();
        Motion.Entrance(editor, 0, 10);
    }

    /// <param name="savedMessage">Уведомление о сохранении; null — форма закрыта без изменений.</param>
    /// <param name="reload">
    /// Перечитывать ли список. При смене раздела это делает сам BuildPage:
    /// повторная загрузка здесь поймала бы флаг занятости и оставила бы
    /// индикатор загрузки навсегда.
    /// </param>
    private void CloseAdminEditor(string? savedMessage, bool reload = true)
    {
        if (AdminEditorHost.Visibility != Visibility.Visible) return;

        AdminEditorHost.Children.Clear();
        AdminEditorHost.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;

        if (savedMessage != null) ShowToast(savedMessage, Palette.Accent);
        if (reload && _activePage == "Admin") _ = ReloadAdminResourcesAsync();
    }
}
