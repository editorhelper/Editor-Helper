using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using EditorHelper.Models;
using EditorHelper.Services;
using EditorHelper.Ui;

namespace EditorHelper.Views;

/// <summary>
/// Форма карточки каталога: создание и редактирование. Живёт внутри главного
/// окна как обычная страница раздела Admin, а не отдельным окном.
///
/// Все поля уходят на сервер одним сохранением, сервер проверяет права и
/// содержимое — клиентская валидация нужна только чтобы не отправлять
/// заведомо неполную форму.
///
/// Источник файла выбирается одним из четырёх способов, которые поддерживает
/// сервер: документ в канале Telegram, внешняя ссылка, файл в хранилище сервера
/// или «без файла» — карточку можно завести раньше, чем появился архив.
/// Обложка грузится отдельным запросом после сохранения, потому что путь к ней
/// привязан к идентификатору карточки.
/// </summary>
public partial class AdminEditorView : UserControl
{
    private static readonly string[] ResourceTypes =
    {
        "Script", "Plugin", "Preset", "Overlay", "Sound", "Texture", "Template", "Extension", "Other"
    };

    private static readonly string[] Applications = { "After Effects", "Premiere Pro", "Photoshop" };

    private static readonly string[] InstallationTypes =
    {
        "Manual", "CopyToFolder", "Installer", "Script", "Extension"
    };

    private const int MaxLinks = 8;

    private readonly AdminService _admin;
    private readonly AdminResource? _current;
    private readonly Action<string?> _onClosed;

    private readonly TextBox _title = CreateBox();
    private readonly TextBox _description = CreateBox();
    private readonly TextBox _longDescription = CreateArea(110);
    private readonly TextBox _category = CreateBox();
    private readonly TextBox _version = CreateBox();
    private readonly TextBox _author = CreateBox();
    private readonly TextBox _slug = CreateBox();
    private readonly TextBox _tags = CreateBox();
    private readonly TextBox _supported = CreateBox();
    private readonly TextBox _documentation = CreateBox();
    private readonly TextBox _note = CreateArea(70);
    private readonly TextBox _externalUrl = CreateBox();
    private readonly TextBox _telegramLink = CreateBox();

    private readonly ComboBox _type = CreateCombo(ResourceTypes);
    private readonly ComboBox _application = CreateCombo(Applications);
    private readonly ComboBox _installation = CreateCombo(InstallationTypes);
    private readonly ComboBox _status = CreateCombo(new[] { "Черновик", "Опубликован", "Скрыт" });
    private readonly ComboBox _telegramDocuments = CreateCombo(Array.Empty<string>());

    private readonly CheckBox _featured = new() { Content = "Показывать в подборке на главной" };
    private readonly CheckBox _free = new() { Content = "Бесплатно", IsChecked = true };

    private readonly RadioButton _sourceNone = CreateSourceRadio("Без файла");
    private readonly RadioButton _sourceTelegram = CreateSourceRadio("Документ в канале Telegram");
    private readonly RadioButton _sourceHttp = CreateSourceRadio("Внешняя ссылка (http/https)");
    private readonly RadioButton _sourceLocal = CreateSourceRadio("Файл в хранилище сервера");

    private readonly StackPanel _nonePanel = new();
    private readonly StackPanel _linksHost = new();
    private readonly StackPanel _telegramPanel = new();
    private readonly StackPanel _httpPanel = new();
    private readonly StackPanel _localPanel = new();

    private readonly TextBlock _sourceState = CreateStateText();
    private readonly TextBlock _bannerState = CreateStateText();
    private readonly Image _bannerPreview = new()
    {
        Height = 120,
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private string? _bannerFileName;
    private byte[]? _bannerBytes;
    private ImageSource? _bannerImage;
    private AdminFileSource _source = new();
    private List<AdminTelegramDocument> _documents = new();
    private bool _busy;

    /// <param name="onClosed">
    /// Вызывается при закрытии формы: сообщение для уведомления, если карточка
    /// сохранена, или null, если пользователь отказался от изменений.
    /// </param>
    public AdminEditorView(AdminService admin, AdminResource? current, Action<string?> onClosed)
    {
        InitializeComponent();

        _admin = admin;
        _current = current;
        _onClosed = onClosed;

        HeaderTitle.Text = current == null ? "Новая карточка" : "Редактирование карточки";
        SaveButton.Content = current == null ? "Создать карточку" : "Сохранить изменения";

        BuildForm();
        LoadValues();
        AttachPreviewUpdates();
        UpdatePreview();

        Loaded += (_, _) =>
        {
            _title.Focus();
            if (_sourceTelegram.IsChecked == true) _ = LoadTelegramDocumentsAsync();
        };
    }

    // ---------------------------------------------------------------- Форма

    private void BuildForm()
    {
        FormPanel.Children.Add(SectionHeader("Основное"));
        FormPanel.Children.Add(Labeled("Название *", _title));
        FormPanel.Children.Add(Labeled("Краткое описание — текст в карточке каталога *", _description));
        FormPanel.Children.Add(Labeled("Полное описание — страница ресурса", _longDescription));

        FormPanel.Children.Add(TwoColumns(
            ("Тип *", _type),
            ("Программа *", _application)));

        FormPanel.Children.Add(TwoColumns(
            ("Категория", _category),
            ("Версия", _version)));

        FormPanel.Children.Add(TwoColumns(
            ("Автор или разработчик", _author),
            ("Способ установки", _installation)));

        FormPanel.Children.Add(TwoColumns(
            ("Slug (пусто — из названия)", _slug),
            ("Статус *", _status)));

        FormPanel.Children.Add(Labeled("Теги через запятую", _tags));
        FormPanel.Children.Add(Labeled("Поддерживаемые версии через запятую", _supported));

        var flags = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        _featured.Margin = new Thickness(0, 0, 26, 0);
        flags.Children.Add(_featured);
        flags.Children.Add(_free);
        FormPanel.Children.Add(flags);

        FormPanel.Children.Add(SectionHeader("Ссылки"));
        FormPanel.Children.Add(Labeled("Документация или инструкция", _documentation,
            "Только http/https. Внутренние адреса сервер отклоняет."));
        FormPanel.Children.Add(LinksEditor());

        FormPanel.Children.Add(SectionHeader("Файл для скачивания"));
        FormPanel.Children.Add(SourceEditor());

        FormPanel.Children.Add(SectionHeader("Обложка"));
        FormPanel.Children.Add(BannerEditor());

        FormPanel.Children.Add(SectionHeader("Служебное"));
        FormPanel.Children.Add(Labeled("Внутренняя заметка — пользователям не показывается", _note));
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = Palette.TextPrimary,
        Margin = new Thickness(0, 24, 0, 12)
    };

    private static TextBox CreateBox() => new() { FontSize = 13 };

    private static TextBox CreateArea(double height) => new()
    {
        FontSize = 13,
        Height = height,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalContentAlignment = VerticalAlignment.Top
    };

    private static ComboBox CreateCombo(string[] items)
    {
        var combo = new ComboBox { FontSize = 12.5, MaxDropDownHeight = 260 };
        foreach (var item in items) combo.Items.Add(item);
        if (items.Length > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static RadioButton CreateSourceRadio(string label) => new()
    {
        Content = label,
        GroupName = "SourceKind",
        Margin = new Thickness(0, 0, 22, 0)
    };

    private static TextBlock CreateStateText() => new()
    {
        FontSize = 11.5,
        Foreground = Palette.TextMuted,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 0)
    };

    /// <summary>
    /// Поле ввода в стеклянной рамке — тот же вид, что в настройках.
    /// Список получает стиль целиком: его шаблон уже рисует границу и скругление,
    /// поэтому оборачивать его в ещё одну рамку нельзя (получился бы двойной контур).
    /// </summary>
    private static FrameworkElement Framed(FrameworkElement input)
    {
        if (input is ComboBox combo)
        {
            combo.Style = (Style)Application.Current.FindResource("GlassComboBox");
            return combo;
        }

        if (input is TextBox box) box.Style = (Style)Application.Current.FindResource("GlassTextBox");

        return new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = input
        };
    }

    private static FrameworkElement Labeled(string label, FrameworkElement input, string? hint = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(2, 0, 0, 6)
        });
        panel.Children.Add(Framed(input));

        if (hint != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = Palette.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0)
            });
        }

        return panel;
    }

    private FrameworkElement TwoColumns(
        (string Label, FrameworkElement Input) left, (string Label, FrameworkElement Input) right)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftPanel = (StackPanel)Labeled(left.Label, left.Input);
        leftPanel.Margin = new Thickness(0, 0, 10, 0);
        var rightPanel = (StackPanel)Labeled(right.Label, right.Input);
        rightPanel.Margin = new Thickness(10, 0, 0, 0);

        Grid.SetColumn(leftPanel, 0);
        Grid.SetColumn(rightPanel, 1);
        grid.Children.Add(leftPanel);
        grid.Children.Add(rightPanel);
        return grid;
    }

    // ---------------------------------------------------------------- Дополнительные ссылки

    private FrameworkElement LinksEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        panel.Children.Add(new TextBlock
        {
            Text = $"Дополнительные ссылки (до {MaxLinks}): сайт автора, демо, changelog",
            FontSize = 11.5,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(2, 0, 0, 8)
        });

        panel.Children.Add(_linksHost);

        var add = new Button
        {
            Content = "Добавить ссылку",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 14)
        };
        add.Click += (_, _) =>
        {
            if (_linkRows.Count >= MaxLinks)
            {
                SetStatus($"Больше {MaxLinks} ссылок добавить нельзя.");
                return;
            }
            AddLinkRow("", "");
        };
        panel.Children.Add(add);

        return panel;
    }

    private sealed class LinkRow
    {
        public required TextBox Label { get; init; }
        public required TextBox Url { get; init; }
    }

    private readonly List<LinkRow> _linkRows = new();

    private void AddLinkRow(string label, string url)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelBox = CreateBox();
        labelBox.Text = label;
        labelBox.ToolTip = "Название ссылки";
        var urlBox = CreateBox();
        urlBox.Text = url;
        urlBox.ToolTip = "https://…";

        var remove = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Убрать ссылку"
        };

        var labelFrame = Framed(labelBox);
        labelFrame.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(labelFrame);

        var urlFrame = Framed(urlBox);
        Grid.SetColumn(urlFrame, 1);
        row.Children.Add(urlFrame);

        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);

        var link = new LinkRow { Label = labelBox, Url = urlBox };

        remove.Click += (_, _) =>
        {
            _linkRows.Remove(link);
            _linksHost.Children.Remove(row);
        };

        _linkRows.Add(link);
        _linksHost.Children.Add(row);
    }

    private List<(string Label, string Url)> CollectLinks() =>
        _linkRows.Select(row => (row.Label.Text.Trim(), row.Url.Text.Trim())).ToList();

    // ---------------------------------------------------------------- Источник файла

    private FrameworkElement SourceEditor()
    {
        var panel = new StackPanel();

        var kinds = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        _sourceNone.Margin = new Thickness(0, 0, 22, 6);
        _sourceTelegram.Margin = new Thickness(0, 0, 22, 6);
        _sourceHttp.Margin = new Thickness(0, 0, 22, 6);
        _sourceLocal.Margin = new Thickness(0, 0, 0, 6);
        kinds.Children.Add(_sourceNone);
        kinds.Children.Add(_sourceTelegram);
        kinds.Children.Add(_sourceHttp);
        kinds.Children.Add(_sourceLocal);
        panel.Children.Add(kinds);

        _sourceNone.Checked += (_, _) => ShowSourcePanel("none");
        _sourceTelegram.Checked += (_, _) => ShowSourcePanel("telegram");
        _sourceHttp.Checked += (_, _) => ShowSourcePanel("http");
        _sourceLocal.Checked += (_, _) => ShowSourcePanel("local");

        panel.Children.Add(_nonePanel);
        panel.Children.Add(_telegramPanel);
        panel.Children.Add(_httpPanel);
        panel.Children.Add(_localPanel);
        panel.Children.Add(_sourceState);

        _nonePanel.Visibility = Visibility.Collapsed;
        _telegramPanel.Visibility = Visibility.Collapsed;
        _httpPanel.Visibility = Visibility.Collapsed;
        _localPanel.Visibility = Visibility.Collapsed;

        BuildNonePanel();
        BuildTelegramPanel();
        BuildHttpPanel();
        BuildLocalPanel();

        return panel;
    }

    private void BuildNonePanel()
    {
        _nonePanel.Children.Add(new TextBlock
        {
            Text = "Карточка сохранится без файла для скачивания. Кнопка загрузки " +
                   "у пользователей будет недоступна, пока источник не добавлен, — " +
                   "поэтому опубликовать такую карточку сервер не даст.",
            FontSize = 11.5,
            Foreground = Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 14),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left
        });
    }

    private void BuildTelegramPanel()
    {
        _telegramPanel.Children.Add(Labeled("Ссылка на пост канала", _telegramLink,
            "https://t.me/канал/123 — бот находит документ, если видел этот пост."));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };

        var find = new Button
        {
            Content = "Найти по ссылке",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        find.Click += async (_, _) => await FindTelegramDocumentAsync();
        buttons.Children.Add(find);

        var upload = new Button
        {
            Content = "Отправить файл в канал",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        upload.Click += async (_, _) => await UploadToTelegramAsync();
        buttons.Children.Add(upload);

        var refresh = new Button
        {
            Content = "Обновить список",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7)
        };
        refresh.Click += async (_, _) => await LoadTelegramDocumentsAsync();
        buttons.Children.Add(refresh);

        _telegramPanel.Children.Add(buttons);
        _telegramPanel.Children.Add(Labeled("Замеченные ботом документы", _telegramDocuments));

        _telegramDocuments.SelectionChanged += (_, _) => ApplyTelegramSelection();
    }

    private void BuildHttpPanel()
    {
        _httpPanel.Children.Add(Labeled("URL архива", _externalUrl,
            "Файл останется на вашем хостинге; сервер проверит доступность и размер."));

        var check = new Button
        {
            Content = "Проверить ссылку",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 14)
        };
        check.Click += async (_, _) => await CheckExternalAsync();
        _httpPanel.Children.Add(check);
    }

    private void BuildLocalPanel()
    {
        var pick = new Button
        {
            Content = "Загрузить файл на сервер",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4)
        };
        pick.Click += async (_, _) => await UploadLocalFileAsync();

        _localPanel.Children.Add(pick);
        _localPanel.Children.Add(new TextBlock
        {
            Text = "Лимит сервера — 100 МБ. Файл сохраняется в хранилище сервера и раздаётся через API.",
            FontSize = 11,
            Foreground = Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 14)
        });
    }

    private void ShowSourcePanel(string kind)
    {
        _nonePanel.Visibility = kind == "none" ? Visibility.Visible : Visibility.Collapsed;
        _telegramPanel.Visibility = kind == "telegram" ? Visibility.Visible : Visibility.Collapsed;
        _httpPanel.Visibility = kind == "http" ? Visibility.Visible : Visibility.Collapsed;
        _localPanel.Visibility = kind == "local" ? Visibility.Visible : Visibility.Collapsed;
        DescribeCurrentSource();
    }

    // ---------------------------------------------------------------- Обложка

    private FrameworkElement BannerEditor()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };

        var cover = new Border
        {
            Background = Palette.CoverPlaceholder,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Height = 120,
            Margin = new Thickness(0, 0, 0, 10),
            Child = _bannerPreview
        };
        panel.Children.Add(cover);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };

        var pick = new Button
        {
            Content = "Выбрать изображение",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 10, 0)
        };
        pick.Click += (_, _) => PickBanner();
        buttons.Children.Add(pick);

        var clear = new Button
        {
            Content = "Убрать выбранное",
            Style = (Style)FindResource("GlassButtonSmall"),
            Padding = new Thickness(14, 7, 14, 7)
        };
        clear.Click += (_, _) =>
        {
            _bannerBytes = null;
            _bannerFileName = null;
            _bannerPreview.Source = _current == null ? null : _bannerImage;
            _bannerState.Text = _current?.Banner == null
                ? "Обложки нет — карточка покажет заглушку с типом ресурса."
                : "Новое изображение не выбрано; сохранена прежняя обложка.";
            UpdatePreview();
        };
        buttons.Children.Add(clear);

        panel.Children.Add(buttons);
        panel.Children.Add(_bannerState);
        return panel;
    }

    private void PickBanner()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Обложка карточки",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length == 0)
            {
                SetStatus("Файл обложки пустой.");
                return;
            }
            if (info.Length > 5 * 1024 * 1024)
            {
                SetStatus($"Обложка {info.Length / 1024 / 1024} МБ больше лимита 5 МБ.");
                return;
            }

            _bannerBytes = File.ReadAllBytes(dialog.FileName);
            _bannerFileName = Path.GetFileName(dialog.FileName);
            _bannerImage = DecodeImage(_bannerBytes);
            _bannerPreview.Source = _bannerImage;
            _bannerState.Text = $"Выбрано: {_bannerFileName} ({Format.FileSize(info.Length)}). Загрузится при сохранении.";
            UpdatePreview();
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось прочитать изображение: " + ex.Message);
        }
    }

    private static BitmapImage? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = 640;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- Заполнение

    private void LoadValues()
    {
        var card = _current;
        if (card == null)
        {
            _version.Text = "1.0.0";
            _status.SelectedIndex = 0;
            _sourceNone.IsChecked = true;
            ShowSourcePanel("none");
            _bannerState.Text = "Обложки нет — карточка покажет заглушку с типом ресурса.";
            return;
        }

        _title.Text = card.Title;
        _description.Text = card.Description;
        _longDescription.Text = card.LongDescription ?? "";
        _category.Text = card.Category ?? "";
        _version.Text = string.IsNullOrWhiteSpace(card.Version) ? "1.0.0" : card.Version;
        _author.Text = card.Author ?? "";
        _slug.Text = card.Slug;
        _tags.Text = string.Join(", ", card.Tags);
        _supported.Text = string.Join(", ", card.SupportedVersions);
        _documentation.Text = card.DocumentationUrl ?? "";
        _note.Text = card.Note ?? "";
        _featured.IsChecked = card.IsFeatured;
        _free.IsChecked = card.IsFree;

        SelectByText(_type, card.Type);
        SelectByText(_application, card.Application);
        SelectByText(_installation, card.InstallationType);
        _status.SelectedIndex = card.Status switch { "published" => 1, "hidden" => 2, _ => 0 };

        foreach (var link in card.Links) AddLinkRow(link.Label, link.Url);

        _source = card.Source == null ? new AdminFileSource() : CloneSource(card.Source);
        switch ((_source.Kind ?? "local").ToLowerInvariant())
        {
            case "http":
                _sourceHttp.IsChecked = true;
                _externalUrl.Text = _source.ExternalUrl ?? "";
                ShowSourcePanel("http");
                break;
            case "telegram" when HasTelegramFile:
                _sourceTelegram.IsChecked = true;
                ShowSourcePanel("telegram");
                break;
            case "local" when !string.IsNullOrWhiteSpace(_source.LocalPath):
                _sourceLocal.IsChecked = true;
                ShowSourcePanel("local");
                break;
            default:
                _sourceNone.IsChecked = true;
                ShowSourcePanel("none");
                break;
        }

        // Сервер меняет источник только если он передан, поэтому «без файла»
        // для карточки с уже привязанным архивом выглядело бы как сохранение,
        // которое ничего не делает.
        _sourceNone.IsEnabled = !HasBoundSource;
        _sourceNone.ToolTip = HasBoundSource
            ? "Файл уже привязан к карточке — выберите другой источник, чтобы его заменить."
            : null;

        _bannerState.Text = card.Banner == null
            ? "Обложки нет — карточка покажет заглушку с типом ресурса."
            : $"Сохранена обложка {card.Banner.FileName ?? ""}. Выберите изображение, чтобы заменить её.";

        if (card.Banner != null) _ = LoadBannerPreviewAsync(card.Id);
    }

    private bool HasTelegramFile => !string.IsNullOrWhiteSpace(_source.TelegramFileId);

    /// <summary>К карточке уже привязан файл, который сервер раздаёт пользователям.</summary>
    private bool HasBoundSource => _source.Kind?.ToLowerInvariant() switch
    {
        "telegram" => HasTelegramFile,
        "http" => !string.IsNullOrWhiteSpace(_source.ExternalUrl),
        "local" => !string.IsNullOrWhiteSpace(_source.LocalPath),
        _ => false
    };

    private async Task LoadBannerPreviewAsync(string id)
    {
        try
        {
            var bytes = await _admin.FetchBannerAsync(id);
            if (bytes == null || _bannerBytes != null) return;

            _bannerImage = DecodeImage(bytes);
            if (_bannerImage == null) return;

            _bannerPreview.Source = _bannerImage;
            UpdatePreview();
        }
        catch
        {
            // Обложка не загрузилась — предпросмотр покажет заглушку, это не ошибка формы
        }
    }

    private static AdminFileSource CloneSource(AdminFileSource source) => new()
    {
        Kind = source.Kind,
        TelegramFileId = source.TelegramFileId,
        TelegramFileUniqueId = source.TelegramFileUniqueId,
        TelegramMessageId = source.TelegramMessageId,
        TelegramChatId = source.TelegramChatId,
        ExternalUrl = source.ExternalUrl,
        LocalPath = source.LocalPath,
        FileName = source.FileName,
        SizeBytes = source.SizeBytes,
        Sha256 = source.Sha256
    };

    private static void SelectByText(ComboBox combo, string? value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private void DescribeCurrentSource()
    {
        _sourceState.Text = SelectedSourceKind() switch
        {
            "telegram" when HasTelegramFile =>
                $"Документ Telegram: {_source.FileName ?? _source.TelegramFileId}, {Format.FileSize(_source.SizeBytes)}.",
            "http" when !string.IsNullOrWhiteSpace(_source.ExternalUrl) =>
                $"Внешняя ссылка: {_source.ExternalUrl}" +
                (_source.SizeBytes > 0 ? $", {Format.FileSize(_source.SizeBytes)}." : "."),
            "local" when !string.IsNullOrWhiteSpace(_source.LocalPath) =>
                $"Файл на сервере: {_source.FileName ?? _source.LocalPath}, {Format.FileSize(_source.SizeBytes)}.",
            "none" => "Файл не привязан: карточку можно сохранить черновиком.",
            _ => "Источник выбран, но файл ещё не указан."
        };
    }

    // ---------------------------------------------------------------- Действия источника

    private async Task LoadTelegramDocumentsAsync()
    {
        await RunAsync(async () =>
        {
            _documents = await _admin.TelegramDocumentsAsync();

            _telegramDocuments.Items.Clear();
            foreach (var document in _documents)
            {
                var bound = document.BoundResourceId != null && document.BoundResourceId != _current?.Id
                    ? " · занят другой карточкой" : "";
                var limit = document.WithinBotLimit ? "" : " · больше лимита бота";
                _telegramDocuments.Items.Add(
                    $"{document.FileName ?? document.FileId} — {Format.FileSize(document.SizeBytes)}{bound}{limit}");
            }

            SetStatus(_documents.Count == 0
                ? "Бот ещё не видел ни одного документа в канале."
                : $"Найдено документов: {_documents.Count}. Выберите файл для карточки.");
        });
    }

    private async Task FindTelegramDocumentAsync()
    {
        var url = _telegramLink.Text.Trim();
        if (url.Length == 0)
        {
            SetStatus("Вставьте ссылку на пост канала.");
            return;
        }

        await RunAsync(async () =>
        {
            var found = await _admin.CheckTelegramLinkAsync(url);
            _source = new AdminFileSource
            {
                Kind = "telegram",
                TelegramFileId = found.FileId,
                FileName = found.FileName,
                SizeBytes = found.SizeBytes
            };
            DescribeCurrentSource();
            SetStatus($"Документ найден: {found.FileName}, {Format.FileSize(found.SizeBytes)}.");
        });
    }

    private async Task UploadToTelegramAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Файл для отправки в канал",
            Filter = "Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await RunAsync(async () =>
        {
            await using var stream = File.OpenRead(dialog.FileName);
            var uploaded = await _admin.UploadToTelegramAsync(Path.GetFileName(dialog.FileName), stream);

            _source = new AdminFileSource
            {
                Kind = "telegram",
                TelegramFileId = uploaded.FileId,
                FileName = uploaded.FileName,
                SizeBytes = uploaded.SizeBytes
            };
            DescribeCurrentSource();
            SetStatus($"Файл отправлен в канал: {uploaded.FileName}, {Format.FileSize(uploaded.SizeBytes)}.");
        });
    }

    private async Task CheckExternalAsync()
    {
        var url = _externalUrl.Text.Trim();
        if (url.Length == 0)
        {
            SetStatus("Укажите URL архива.");
            return;
        }
        if (UrlError(url) is { } syntax)
        {
            SetStatus(syntax);
            return;
        }

        await RunAsync(async () =>
        {
            var probe = await _admin.CheckExternalAsync(url);
            _source = new AdminFileSource
            {
                Kind = "http",
                ExternalUrl = url,
                FileName = probe.FileName ?? _source.FileName,
                SizeBytes = probe.SizeBytes > 0 ? probe.SizeBytes : _source.SizeBytes
            };
            DescribeCurrentSource();
            SetStatus(probe.Available
                ? $"Ссылка доступна: {probe.FileName ?? "файл"}, {Format.FileSize(probe.SizeBytes)}."
                : $"Ссылка недоступна: {probe.Message}");
        });
    }

    private async Task UploadLocalFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Файл архива",
            Filter = "Все файлы|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 100L * 1024 * 1024)
        {
            SetStatus($"Файл {info.Length / 1024 / 1024} МБ больше лимита загрузки 100 МБ.");
            return;
        }

        await RunAsync(async () =>
        {
            await using var stream = File.OpenRead(dialog.FileName);
            var uploaded = await _admin.UploadFileAsync(info.Name, stream);

            _source = new AdminFileSource
            {
                Kind = uploaded.Kind,
                LocalPath = uploaded.LocalPath,
                FileName = uploaded.FileName,
                SizeBytes = uploaded.SizeBytes,
                Sha256 = uploaded.Sha256
            };
            DescribeCurrentSource();
            SetStatus($"Файл загружен на сервер: {uploaded.FileName}, {Format.FileSize(uploaded.SizeBytes)}.");
        });
    }

    private void ApplyTelegramSelection()
    {
        var index = _telegramDocuments.SelectedIndex;
        if (index < 0 || index >= _documents.Count) return;

        var document = _documents[index];
        _source = new AdminFileSource
        {
            Kind = "telegram",
            TelegramFileId = document.FileId,
            TelegramFileUniqueId = document.FileUniqueId,
            TelegramMessageId = document.MessageId,
            FileName = document.FileName,
            SizeBytes = document.SizeBytes
        };
        DescribeCurrentSource();
    }

    // ---------------------------------------------------------------- Предпросмотр

    /// <summary>
    /// Подписывает поля, влияющие на карточку, на обновление предпросмотра.
    /// Делается один раз после заполнения, чтобы LoadValues не дёргал
    /// перерисовку на каждое присваивание.
    /// </summary>
    private void AttachPreviewUpdates()
    {
        foreach (var box in new[] { _title, _description, _category, _version, _slug })
            box.TextChanged += (_, _) => UpdatePreview();

        foreach (var combo in new[] { _type, _application, _status })
            combo.SelectionChanged += (_, _) => UpdatePreview();
    }

    private void UpdatePreview()
    {
        var resource = new Resource
        {
            Id = _current?.Id ?? "preview",
            Slug = string.IsNullOrWhiteSpace(_slug.Text) ? _title.Text : _slug.Text,
            Title = string.IsNullOrWhiteSpace(_title.Text) ? "Название карточки" : _title.Text,
            Description = string.IsNullOrWhiteSpace(_description.Text)
                ? "Краткое описание появится здесь"
                : _description.Text,
            LongDescription = string.IsNullOrWhiteSpace(_longDescription.Text) ? null : _longDescription.Text,
            Type = Enum.TryParse<ResourceType>(_type.SelectedItem?.ToString(), out var type) ? type : ResourceType.Other,
            Application = _application.SelectedItem?.ToString() ?? "",
            Category = _category.Text.Trim(),
            Version = string.IsNullOrWhiteSpace(_version.Text) ? "1.0.0" : _version.Text.Trim(),
            Author = string.IsNullOrWhiteSpace(_author.Text) ? null : _author.Text.Trim()
        };

        var card = ResourceCardView.Create(resource, null, null, _bannerImage);
        card.Margin = new Thickness(0);
        card.Cursor = System.Windows.Input.Cursors.Arrow;
        PreviewHost.Content = card;

        var status = _status.SelectedIndex switch { 1 => "опубликована", 2 => "скрыта", _ => "черновик" };
        PreviewNote.Text = $"Статус после сохранения: {status}. В каталоге приложения видны только опубликованные карточки.";
    }

    // ---------------------------------------------------------------- Сохранение

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (Validate() is { } invalid)
        {
            SetStatus(invalid.Message);
            invalid.Field.Focus();
            return;
        }

        var input = BuildInput();
        await RunAsync(async () =>
        {
            var saved = _current == null
                ? await _admin.CreateAsync(input)
                : await _admin.UpdateAsync(_current.Id, input);

            if (_bannerBytes != null && _bannerFileName != null)
            {
                await using var banner = new MemoryStream(_bannerBytes);
                await _admin.UploadBannerAsync(saved.Id, _bannerFileName, banner);
            }

            var message = _current == null
                ? $"Карточка «{saved.Title}» создана ({StatusLabel(saved.Status)})"
                : $"Карточка «{saved.Title}» сохранена ({StatusLabel(saved.Status)})";
            _onClosed(message);
        }, closeOnSuccess: false);
    }

    private AdminResource BuildInput()
    {
        var kind = SelectedSourceKind();

        return new AdminResource
        {
            Id = _current?.Id ?? "",
            Slug = _slug.Text.Trim(),
            Title = _title.Text.Trim(),
            Description = _description.Text.Trim(),
            LongDescription = EmptyToNull(_longDescription.Text),
            Type = _type.SelectedItem?.ToString() ?? "Other",
            Application = _application.SelectedItem?.ToString() ?? "",
            Category = _category.Text.Trim(),
            Version = string.IsNullOrWhiteSpace(_version.Text) ? "1.0.0" : _version.Text.Trim(),
            Author = EmptyToNull(_author.Text),
            Tags = SplitList(_tags.Text),
            SupportedVersions = SplitList(_supported.Text),
            InstallationType = _installation.SelectedItem?.ToString() ?? "Manual",
            IsFeatured = _featured.IsChecked == true,
            IsFree = _free.IsChecked == true,
            Status = _status.SelectedIndex switch { 1 => "published", 2 => "hidden", _ => "draft" },
            DocumentationUrl = EmptyToNull(_documentation.Text),
            Note = EmptyToNull(_note.Text),
            FileName = _source.FileName,
            FileSize = _source.SizeBytes,
            Checksum = string.IsNullOrWhiteSpace(_source.Sha256) ? _current?.Checksum : $"sha256:{_source.Sha256}",
            // Без источника отправляем null: сервер оставляет поле как есть,
            // а пустой объект вида {kind:"telegram"} он отвергнул бы как неполный
            Source = kind == "none" ? null : BuildSource(kind),
            Banner = _current?.Banner,
            PublishedAt = _current?.PublishedAt ?? default,
            UpdatedAt = _current?.UpdatedAt ?? default
        };
    }

    private AdminFileSource BuildSource(string kind)
    {
        var source = CloneSource(_source);
        source.Kind = kind;

        switch (kind)
        {
            case "http":
                source.ExternalUrl = _externalUrl.Text.Trim();
                source.TelegramFileId = null;
                source.LocalPath = null;
                break;
            case "local":
                source.ExternalUrl = null;
                source.TelegramFileId = null;
                break;
            default:
                source.ExternalUrl = null;
                source.LocalPath = null;
                break;
        }

        return source;
    }

    private string SelectedSourceKind() =>
        _sourceNone.IsChecked == true ? "none"
        : _sourceHttp.IsChecked == true ? "http"
        : _sourceLocal.IsChecked == true ? "local"
        : "telegram";

    private (string Message, FrameworkElement Field)? Validate()
    {
        if (string.IsNullOrWhiteSpace(_title.Text))
            return ("Название обязательно.", _title);
        if (string.IsNullOrWhiteSpace(_description.Text))
            return ("Краткое описание обязательно — его видно в карточке каталога.", _description);

        var documentation = _documentation.Text.Trim();
        if (documentation.Length > 0 && UrlError(documentation) is { } docError)
            return ($"Ссылка на документацию: {docError}", _documentation);

        foreach (var (label, url) in CollectLinks())
        {
            if (label.Length == 0 && url.Length == 0) continue;
            if (label.Length == 0 || url.Length == 0)
                return ("У дополнительной ссылки нужны и название, и адрес.", _linksHost);
            if (UrlError(url) is { } linkError) return ($"Ссылка «{label}»: {linkError}", _linksHost);
        }

        return SelectedSourceKind() switch
        {
            "http" when _externalUrl.Text.Trim().Length == 0 =>
                ("Для внешней ссылки нужен URL архива.", _externalUrl),
            "http" when UrlError(_externalUrl.Text.Trim()) is { } error =>
                ($"URL архива: {error}", _externalUrl),
            "local" when string.IsNullOrWhiteSpace(_source.LocalPath) =>
                ("Загрузите файл в хранилище сервера или выберите «Без файла».", _localPanel),
            "telegram" when !HasTelegramFile =>
                ("Выберите документ Telegram: найдите по ссылке, отправьте файл в канал " +
                 "или возьмите из списка. Либо выберите «Без файла».", _telegramPanel),
            _ => null
        };
    }

    private static string? UrlError(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "не разбирается как абсолютный URL.";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return $"схема {uri.Scheme} не поддерживается — только http и https.";
        if (uri.UserInfo.Length > 0)
            return "адреса с логином и паролем (user@host) запрещены.";
        return null;
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string StatusLabel(string status) => status switch
    {
        "published" => "опубликована",
        "hidden" => "скрыта",
        _ => "черновик"
    };

    // ---------------------------------------------------------------- Служебное

    private async Task RunAsync(Func<Task> action, bool closeOnSuccess = true)
    {
        if (_busy) return;

        _busy = true;
        SaveButton.IsEnabled = false;
        SetStatus("Выполняем запрос…");

        try
        {
            await action();
            if (closeOnSuccess) _onClosed(null);
        }
        catch (AdminApiException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("Сервер недоступен: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
        }
    }

    private void SetStatus(string message) => FooterStatus.Text = message;

    private void BackButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Close() => _onClosed(null);
}
