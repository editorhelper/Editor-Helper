using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using EditorHelper.Models;
using EditorHelper.Services;
using EditorHelper.Ui;
using EditorHelper.Views;

namespace EditorHelper;

/// <summary>
/// Главное окно приложения: оболочка Premium Cosmic — живой космический фон,
/// стеклянные панели, верхняя панель, боковая навигация и область содержимого.
/// </summary>
public partial class MainWindow : Window
{
    private const string SearchPlaceholder = "Search tools, plugins, presets...";

    private readonly CatalogService _catalogService;
    // Пересоздаётся при выходе из аккаунта: избранное старого пользователя
    // не должно оставаться в памяти после logout
    private FavoritesService _favoritesService;
    private readonly LoadedResourcesService _loadedService;
    private SessionService? _sessionService;
    private AdminService? _adminService;
    private AppSettings _settings;
    private string _activePage = "Home";
    private bool _introDone;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _catalogService = CatalogService.Instance;
        _loadedService = new LoadedResourcesService();
        _favoritesService = CreateFavoritesService(_settings);
        _ = LoadRemoteFavoritesAsync();
        if (_settings.CatalogSource.Equals("remote", StringComparison.OrdinalIgnoreCase))
        {
            _sessionService = new SessionService(_settings.ApiBaseUrl);
            _ = RestoreAccountAsync();
        }

        ApplyEffectsSetting(_settings.EffectsEnabled && !SystemPrefersReducedMotion());
        UpdateSourceBadge();

        // Безрамочное окно при разворачивании должно уважать рабочую область
        // (панель задач) и не выходить за края экрана
        SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?
                .AddHook(MaximizeHook);

            ApplyRoundedCorners(handle);
        };

        // Первая загрузка каталога — в Loaded, а не в конструкторе:
        // в конструкторе ещё не установлен SynchronizationContext,
        // и продолжение после await ушло бы в фоновый поток.
        Loaded += (_, _) =>
        {
            SetActiveNavButton(FindNavButton(_activePage));
            LoadPage("Home");
            PlayIntro();
        };
    }

    private FavoritesService CreateFavoritesService(AppSettings settings)
    {
        if (!settings.CatalogSource.Equals("remote", StringComparison.OrdinalIgnoreCase)) return new FavoritesService();
        _sessionService ??= new SessionService(settings.ApiBaseUrl);
        return new FavoritesService(settings.ApiBaseUrl, _sessionService);
    }

    private async Task LoadRemoteFavoritesAsync()
    {
        try { if (_favoritesService.IsServerBacked) await _favoritesService.LoadAsync(); }
        catch { }
    }

    private async Task RestoreAccountAsync()
    {
        if (_sessionService == null) return;
        try
        {
            var user = await _sessionService.RestoreAsync();
            Dispatcher.Invoke(() => UpdateAccountButton(user));
        }
        catch
        {
            Dispatcher.Invoke(() => UpdateAccountButton(null));
        }
    }

    private void UpdateAccountButton(SessionUser? user)
    {
        if (AccountButton == null) return;
        AccountButton.Content = user == null ? "Sign in" : $"{user.DisplayName} · {RoleBadge(user.Role)}";
        AccountButton.ToolTip = user == null ? "Sign in with Telegram" : $"Account: {user.Role}. Click to sign out.";

        ApplyAdminAccess(user);
    }

    /// <summary>
    /// Раздел Admin виден только роли Owner и только при работе через сервер —
    /// в локальном каталоге админ-API нет. Это удобство навигации: каждое
    /// действие всё равно проверяет роль на сервере, поэтому скрытый пункт
    /// не является защитой и не может ей считаться.
    /// </summary>
    private void ApplyAdminAccess(SessionUser? user)
    {
        if (AdminNavButton == null) return;

        var isOwner = string.Equals(user?.Role, "Owner", StringComparison.OrdinalIgnoreCase);
        var serverBacked = _catalogService.IsRemote;

        _adminService = isOwner && serverBacked && _sessionService != null
            ? new AdminService(_sessionService.BaseUrl, _sessionService)
            : null;

        AdminNavButton.Visibility = _adminService != null ? Visibility.Visible : Visibility.Collapsed;

        // После выхода или потери прав нельзя остаться на странице Admin
        if (_adminService == null && _activePage == "Admin")
            NavigateTo("Home");
    }

    /// <summary>Подпись роли для кнопки аккаунта: Owner — звезда, VIP — кристалл.</summary>
    private static string RoleBadge(string role) => role switch
    {
        "Owner" => "⭐ OWNER",
        "VIP" => "💎 VIP",
        _ => "User"
    };

    // ---------------------------------------------------------------- Оверлей авторизации

    private static readonly TimeSpan AuthPollInterval = TimeSpan.FromSeconds(2);

    private SessionChallenge? _authChallenge;
    private bool _authCompleting;
    private bool _authClosing;
    private DispatcherTimer? _authPoll;

    /// <summary>
    /// Показывает оверлей входа. Космический фон остаётся виден и живёт
    /// под полупрозрачным затемнением, карточка всплывает с масштабом и
    /// свечением. Повторный вызов при открытом оверлее игнорируется.
    /// </summary>
    private void OpenAuthOverlay()
    {
        if (AuthOverlay.Visibility == Visibility.Visible) return;

        _authChallenge = null;
        _authCompleting = false;
        _authClosing = false;
        ShowButtonIdle();
        ResetAuthStatus("Ожидание авторизации", "Нажмите «Продолжить через Telegram», чтобы начать вход.");

        AuthDimBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        AuthDimBrush.Color = Color.FromArgb(0, 0, 0, 0);
        AuthCardFrame.BeginAnimation(UIElement.OpacityProperty, null);
        AuthCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        AuthCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AuthCardLift.BeginAnimation(TranslateTransform.YProperty, null);
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty, null);
        AuthButtonLift.Y = 0;
        AuthCardFrame.Opacity = 1;
        AuthCard.Opacity = 1;
        AuthCardScale.ScaleX = 1;
        AuthCardScale.ScaleY = 1;
        AuthCardLift.Y = 0;

        AuthOverlay.Visibility = Visibility.Visible;
        AuthOverlay.IsHitTestVisible = true;
        AuthTelegramButton.IsEnabled = true;

        if (Motion.Enabled)
        {
            AuthDimBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(Color.FromArgb(0xB4, 0, 0, 0), TimeSpan.FromMilliseconds(Motion.Slow)) { EasingFunction = Motion.EaseOut });
            AuthCardScale.ScaleX = 0.96;
            AuthCardScale.ScaleY = 0.96;
            AuthCardLift.Y = 18;
            AuthCardFrame.Opacity = 0;

            AuthCardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.Slow)) { EasingFunction = Motion.EaseOut });
            AuthCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.Slow)) { EasingFunction = Motion.EaseOut });
            AuthCardLift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.Slow)) { EasingFunction = Motion.EaseOut });
            AuthCardFrame.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.Slow)) { EasingFunction = Motion.EaseOut });

            if (AuthPanel is { } panel)
            {
                var i = 0;
                foreach (FrameworkElement child in panel.Children)
                {
                    child.Opacity = 0;
                    child.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.Base + 80))
                    {
                        BeginTime = TimeSpan.FromMilliseconds(120 + i * 45),
                        EasingFunction = Motion.EaseOut
                    });
                    i++;
                }
            }
        }
    }

    /// <summary>Обратная анимация и скрытие оверлея. Активный вход не прерывает.</summary>
    private void CloseAuthOverlay()
    {
        if (AuthOverlay.Visibility != Visibility.Visible || _authClosing) return;
        if (_authCompleting)
        {
            ShowAuthError("Вход выполняется", "Дождитесь завершения проверки авторизации.");
            return;
        }

        _authClosing = true;
        StopAuthPolling();
        StopButtonSpinner();
        StopStatusSpinner();

        void Finish()
        {
            AuthOverlay.Visibility = Visibility.Collapsed;
            AuthOverlay.IsHitTestVisible = false;
            _authClosing = false;
            if (_authChallenge != null)
            {
                var requestId = _authChallenge.RequestId;
                _authChallenge = null;
                _ = TryCancelAuthChallengeAsync(requestId);
            }
        }

        if (!Motion.Enabled)
        {
            Finish();
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Motion.Fast)) { EasingFunction = Motion.EaseOut };
        fade.Completed += (_, _) =>
        {
            AuthCardFrame.BeginAnimation(UIElement.OpacityProperty, null);
            AuthCardFrame.Opacity = 1;
            Finish();
        };
        AuthCardFrame.BeginAnimation(UIElement.OpacityProperty, fade);

        AuthCardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.98, TimeSpan.FromMilliseconds(Motion.Fast)) { EasingFunction = Motion.EaseOut });
        AuthCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.98, TimeSpan.FromMilliseconds(Motion.Fast)) { EasingFunction = Motion.EaseOut });
        AuthCardLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(10, TimeSpan.FromMilliseconds(Motion.Fast)) { EasingFunction = Motion.EaseOut });
        AuthDimBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(0, 0, 0, 0), TimeSpan.FromMilliseconds(Motion.Base)) { EasingFunction = Motion.EaseOut });
        foreach (FrameworkElement child in AuthPanel.Children)
            child.BeginAnimation(UIElement.OpacityProperty, null);
    }

    // ---------------------------------------------------------------- Главная кнопка

    private void AuthTelegramButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!Motion.Enabled || !AuthTelegramButton.IsEnabled) return;
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-1.5, TimeSpan.FromMilliseconds(Motion.Base)) { EasingFunction = Motion.EaseOut });
    }

    private void AuthTelegramButton_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!Motion.Enabled) return;
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.Base)) { EasingFunction = Motion.EaseOut });
    }

    private void AuthTelegramButton_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (!Motion.Enabled || !AuthTelegramButton.IsEnabled) return;
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0.5, TimeSpan.FromMilliseconds(70)) { EasingFunction = Motion.EaseOut });
    }

    private void AuthTelegramButton_PreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (!Motion.Enabled || !AuthTelegramButton.IsEnabled) return;
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-1.5, TimeSpan.FromMilliseconds(Motion.Fast)) { EasingFunction = Motion.Snap });
    }

    /// <summary>Загрузка кнопки: спиннер вместо логотипа, текст этапа, повторные клики исключены.</summary>
    private void ShowButtonBusy(string text)
    {
        AuthButtonBusyText.Text = text;
        AuthButtonIdleContent.Visibility = Visibility.Collapsed;
        AuthButtonBusyContent.Visibility = Visibility.Visible;
        if (Motion.Enabled)
        {
            AuthButtonSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
        AuthButtonLift.BeginAnimation(TranslateTransform.YProperty, null);
        AuthButtonLift.Y = 0;
    }

    private void ShowButtonIdle()
    {
        StopButtonSpinner();
        AuthButtonBusyContent.Visibility = Visibility.Collapsed;
        AuthButtonIdleContent.Visibility = Visibility.Visible;
    }

    private void StopButtonSpinner() =>
        AuthButtonSpin.BeginAnimation(RotateTransform.AngleProperty, null);

    private async Task TryCancelAuthChallengeAsync(string requestId)
    {
        try
        {
            if (_sessionService != null) await _sessionService.CancelTelegramLoginAsync(requestId);
        }
        catch { }
    }

    private async void AuthTelegramButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionService == null || _authCompleting) return;
        AuthTelegramButton.IsEnabled = false;
        ShowButtonBusy("Открываем Telegram…");
        ShowAuthStage(AuthStage.Busy, "Открываем Telegram", "Создаём запрос входа на сервере…");
        try
        {
            var challenge = await _sessionService.StartTelegramLoginAsync();
            _authChallenge = challenge;

            var bot = challenge.BotUsername;
            if (string.IsNullOrWhiteSpace(bot))
            {
                ShowButtonIdle();
                AuthCancelButton.IsEnabled = false;
                ShowAuthError("Не удалось войти", "Сервер не настроен для входа через Telegram. Попробуйте позже.");
                AuthTelegramButton.IsEnabled = true;
                return;
            }

            ShowButtonIdle();
            ShowAuthStage(AuthStage.WaitingConfirm, "Ожидание подтверждения",
                $"Подтвердите вход в Telegram: @{bot} → «Да, войти». Приложение продолжит само.");
            AuthCancelButton.IsEnabled = true;
            StartAuthPolling();

            var link = $"https://t.me/{bot}?start={challenge.RequestId}";
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
            catch
            {
                ShowAuthStage(AuthStage.WaitingConfirm, "Ожидание подтверждения",
                    $"Telegram не открылся автоматически. Откройте вручную: t.me/{bot} и нажмите Start.");
            }

            await PollAuthStatusAsync();
        }
        catch (Exception ex)
        {
            ShowButtonIdle();
            AuthCancelButton.IsEnabled = false;
            ShowAuthError("Не удалось войти", ex.Message);
            AuthTelegramButton.IsEnabled = true;
        }
    }

    private void StartAuthPolling()
    {
        _authPoll ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = AuthPollInterval };
        _authPoll.Tick -= AuthPoll_Tick;
        _authPoll.Tick += AuthPoll_Tick;
        _authPoll.Start();
    }

    private void StopAuthPolling() => _authPoll?.Stop();

    private async void AuthPoll_Tick(object? sender, EventArgs e) => await PollAuthStatusAsync();

    private async Task PollAuthStatusAsync()
    {
        if (_authChallenge == null || _authCompleting || _sessionService == null) return;

        try
        {
            var status = await _sessionService.GetTelegramLoginStatusAsync(_authChallenge.RequestId);
            switch (status)
            {
                case "expired":
                    StopAuthPolling();
                    _authChallenge = null;
                    ResetAuthStatus("Запрос истёк", "Время подтверждения вышло. Начните вход заново.");
                    AuthTelegramButton.IsEnabled = true;
                    return;
                case "declined":
                    StopAuthPolling();
                    _authChallenge = null;
                    ResetAuthStatus("Вход отменён", "Вы отклонили вход в Telegram. Начните заново, если передумали.");
                    AuthTelegramButton.IsEnabled = true;
                    return;
                case "confirmed":
                    await CompleteAuthAsync();
                    break;
            }
        }
        catch
        {
            // Фоновый опрос: единичный сбой сети не должен пугать пользователя,
            // причину покажет ручная проверка
        }
    }

    private async Task CompleteAuthAsync()
    {
        if (_authChallenge == null || _sessionService == null || _authCompleting) return;
        _authCompleting = true;
        StopAuthPolling();
        ShowButtonBusy("Проверяем авторизацию…");
        ShowAuthStage(AuthStage.Busy, "Проверка авторизации", "Получаем сессию с сервера…");
        AuthCancelButton.IsEnabled = false;
        try
        {
            var user = await _sessionService.CompleteTelegramLoginAsync(_authChallenge.RequestId, CancellationToken.None);
            _authChallenge = null;
            _authCompleting = false;
            ShowButtonIdle();
            ShowAuthStage(AuthStage.Success, "Вход выполнен", $"{user.DisplayName} · {RoleBadge(user.Role)}");
            UpdateAccountButton(user);
            await Task.Delay(700);
            CloseAuthOverlay();
            ShowToast($"Signed in as {user.DisplayName} ({user.Role})", Palette.Accent);
        }
        catch (DeviceConflictException ex)
        {
            _authCompleting = false;
            _authChallenge = null;
            StopAuthPolling();
            ShowButtonIdle();
            ShowAuthError("Другое устройство", ex.Message);
            AuthTelegramButton.IsEnabled = true;
            AuthCancelButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            _authCompleting = false;
            _authChallenge = null;
            StopAuthPolling();
            ShowButtonIdle();
            ShowAuthError("Не удалось войти", ex.Message);
            AuthTelegramButton.IsEnabled = true;
            AuthCancelButton.IsEnabled = false;
        }
    }

    private async void AuthCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_authCompleting) return;
        var challenge = _authChallenge;
        StopAuthPolling();
        _authChallenge = null;
        if (challenge != null) await TryCancelAuthChallengeAsync(challenge.RequestId);
        ResetAuthStatus("Ожидание авторизации", "Вход отменён. Нажмите кнопку, чтобы начать заново.");
        AuthTelegramButton.IsEnabled = true;
    }

    private void AuthCloseButton_Click(object sender, RoutedEventArgs e) => CloseAuthOverlay();

    /// <summary>Клик по затемнению закрывает оверлей; карточка перехватывает клик.</summary>
    private void AuthOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, AuthOverlay)) CloseAuthOverlay();
    }

    /// <summary>Клик по карточке не должен закрывать оверлей.</summary>
    private void AuthCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    /// <summary>Escape закрывает оверлей, пока не идёт завершение входа.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AuthOverlay.Visibility == Visibility.Visible)
        {
            if (!_authCompleting)
            {
                CloseAuthOverlay();
                e.Handled = true;
            }
        }
    }

    // ---------------------------------------------------------------- Состояния авторизации

    /// <summary>
    /// Этап входа: один активный статус, своя иконка и индикатор на каждый.
    /// Busy покрывает «Открываем Telegram» и «Проверяем авторизацию» —
    /// различаются заголовком, визуально это одно непрерывное ожидание.
    /// </summary>
    private enum AuthStage { Idle, Busy, WaitingConfirm, Success, Error }

    private void ShowAuthStage(AuthStage stage, string title, string hint)
    {
        AuthStatusTitle.Text = title;
        AuthStatusHint.Text = hint;

        var glyph = stage switch
        {
            AuthStage.WaitingConfirm => "\uE823",  // Segoe MDL2: Clock
            _ => "\uE77B"                          // Segoe MDL2: Contact
        };
        AuthStateGlyph.Text = glyph;
        // Busy, Success и Error рисуют собственный индикатор (спиннер, галочка,
        // крест) — глиф в этих состояниях скрыт, иначе иконки наложились бы.
        AuthStateGlyph.Visibility = stage is AuthStage.Idle or AuthStage.WaitingConfirm
            ? Visibility.Visible : Visibility.Collapsed;
        AuthStateSpinner.Visibility = stage == AuthStage.Busy ? Visibility.Visible : Visibility.Collapsed;
        AuthStateCheck.Visibility = stage == AuthStage.Success ? Visibility.Visible : Visibility.Collapsed;
        AuthStateError.Visibility = stage == AuthStage.Error ? Visibility.Visible : Visibility.Collapsed;

        if (stage == AuthStage.Busy && Motion.Enabled)
        {
            AuthStateSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
        else StopStatusSpinner();

        if (stage == AuthStage.Success) Motion.Pulse(AuthStateIconHost, 1.18);

        // Смена текста — короткий мягкий переход, без резких скачков
        if (Motion.Enabled)
        {
            AuthStatusTextHost.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(Motion.Base))
            {
                EasingFunction = Motion.EaseOut
            });
        }
    }

    private void StopStatusSpinner() =>
        AuthStateSpin.BeginAnimation(RotateTransform.AngleProperty, null);

    private void ShowAuthError(string title, string message)
    {
        ShowAuthStage(AuthStage.Error, title, message);
    }

    /// <summary>Возврат к ожиданию: кнопка доступна, отмена скрыта, спиннеры сняты.</summary>
    private void ResetAuthStatus(string title, string message)
    {
        ShowButtonIdle();
        ShowAuthStage(AuthStage.Idle, title, message);
        AuthCancelButton.IsEnabled = false;
    }

    private Popup? _accountMenu;

    private async void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionService == null)
        {
            _sessionService = new SessionService(_settings.ApiBaseUrl);
            await RestoreAccountAsync();
        }
        if (_sessionService.IsAuthenticated && _sessionService.CurrentUser != null)
        {
            if (_accountMenu?.IsOpen == true) { _accountMenu.IsOpen = false; return; }
            ShowAccountMenu(_sessionService.CurrentUser);
            return;
        }

        if (AuthOverlay.Visibility != Visibility.Visible)
            OpenAuthOverlay();
    }

    /// <summary>
    /// Выпадающее меню аккаунта: тёмная карточка в стиле приложения вместо
    /// системного MessageBox. Аватар, имя, роль и выход одним понятным
    /// действием — сессия на сервере отзывается, локальный токен удаляется.
    /// </summary>
    private void ShowAccountMenu(SessionUser user)
    {
        var card = new Border
        {
            Background = Palette.BgElevated,
            BorderBrush = Palette.GlassBorderBright,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8),
            MinWidth = 264,
            MaxWidth = 320,
            Effect = Palette.CardShadowEffect()
        };

        var root = new StackPanel();

        // Шапка: аватар + имя + роль
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 10) };
        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            Background = Palette.BgPrimary,
            CornerRadius = new CornerRadius(18),
            BorderBrush = Palette.GlassBorderBright,
            BorderThickness = new Thickness(1),
            Child = new AvatarImage { Source = LoadAvatar() }
        };
        header.Children.Add(avatar);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        names.Children.Add(new TextBlock
        {
            Text = user.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        names.Children.Add(new TextBlock
        {
            Text = $"Роль: {RoleBadge(user.Role)}",
            FontSize = 11,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 2, 0, 0)
        });
        header.Children.Add(names);
        root.Children.Add(header);

        root.Children.Add(new Separator
        {
            Background = Palette.GlassBorder,
            Height = 1,
            Margin = new Thickness(4, 0, 4, 6)
        });

        root.Children.Add(CreateAccountMenuItem("", "Выйти из аккаунта", async () =>
        {
            if (_accountMenu != null) _accountMenu.IsOpen = false;
            try
            {
                await _sessionService!.LogoutAsync();
                UpdateAccountButton(null);
                _favoritesService = CreateFavoritesService(_settings);
                ShowToast("Вы вышли из аккаунта", Palette.Secondary);
                OpenAuthOverlay();
            }
            catch (Exception ex)
            {
                ShowToast("Не удалось выйти: " + ex.Message, Palette.Accent);
            }
        }));

        card.Child = root;

        _accountMenu = new Popup
        {
            PlacementTarget = AccountButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            HorizontalOffset = -8,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = card
        };
        _accountMenu.Opened += (_, _) => Motion.Entrance(card, 0, 8);
        _accountMenu.IsOpen = true;
        // Esc закрывает меню (Window_PreviewKeyDown не видит Popup без фокуса)
        card.PreviewKeyDown += (_, ke) => { if (ke.Key == Key.Escape) _accountMenu!.IsOpen = false; };
        card.Focusable = true;
        card.Focus();
    }

    private Border CreateAccountMenuItem(string glyph, string label, Action onClick)
    {
        var item = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 9, 10, 9),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Palette.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        item.Child = content;

        item.MouseEnter += (_, _) => item.Background = Palette.GlassHover;
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => onClick();

        return item;
    }

    // ---------------------------------------------------------------- Форма окна

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    /// <summary>
    /// Закругляет углы безрамочного окна средствами DWM (Windows 11).
    ///
    /// Именно DWM, а не AllowsTransparency: последний переключает окно
    /// в программный рендер и лишил бы аппаратного ускорения живой космический
    /// фон с частицами. Если система старше Windows 11, вызов просто вернёт
    /// ошибку и окно останется прямоугольным — это не критично.
    /// </summary>
    private static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference, Marshal.SizeOf<int>());
        }
        catch
        {
            // Закругление — украшение: его отсутствие не должно мешать запуску
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);

    // ---------------------------------------------------------------- Эффекты и доступность

    /// <summary>Системная настройка «уменьшить движение» (SPI_GETCLIENTAREAANIMATION).</summary>
    private static bool SystemPrefersReducedMotion()
    {
        int value = 1;
        try
        {
            SystemParametersInfo(0x1043, 0, ref value, 0);
        }
        catch
        {
            return false;
        }
        return value == 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(int uiAction, int uiParam, ref int pvParam, int fWinIni);

    /// <summary>Включает или выключает все декоративные анимации приложения.</summary>
    private void ApplyEffectsSetting(bool enabled)
    {
        Motion.Enabled = enabled;

        if (enabled) SpaceCanvas.Start();
        else
        {
            SpaceCanvas.Stop();
            SpaceCanvas.InvalidateVisual();
        }

        if (LogoGlow != null) LogoGlow.Opacity = enabled ? 0.4 : 0.25;

        // Оверлей входа мог остаться открытым: его спиннеры должны подчиняться флагу
        if (AuthOverlay.Visibility == Visibility.Visible && !enabled)
        {
            StopStatusSpinner();
            StopButtonSpinner();
        }
    }

    // ---------------------------------------------------------------- Окно

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>
    /// Безрамочное окно при разворачивании по умолчанию закрывает всю панель задач
    /// и выходит за экран. Обрабатываем WM_GETMINMAXINFO и ограничиваем размер
    /// рабочей областью текущего монитора.
    /// </summary>
    private IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        const int MONITOR_DEFAULTTONEAREST = 0x0002;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var placement = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(monitor, ref placement);

            info.ptMaxPosition.X = placement.rcWork.Left - placement.rcMonitor.Left;
            info.ptMaxPosition.Y = placement.rcWork.Top - placement.rcMonitor.Top;
            info.ptMaxSize.X = placement.rcWork.Right - placement.rcWork.Left;
            info.ptMaxSize.Y = placement.rcWork.Bottom - placement.rcWork.Top;

            Marshal.StructureToPtr(info, lParam, true);
        }

        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>Фон ставится на паузу, когда окно свёрнуто, — экономия ресурсов.</summary>
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) SpaceCanvas.Stop();
        else if (Motion.Enabled) SpaceCanvas.Start();
    }

    /// <summary>Параллакс: фон мягко следует за курсором.</summary>
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        SpaceCanvas.UpdateParallax(pos.X, pos.Y, RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    // ---------------------------------------------------------------- Вступление

    /// <summary>
    /// Последовательное появление приложения: окно, свет логотипа,
    /// верхняя панель, навигация, затем контент. Быстро, без задержек доступа.
    /// </summary>
    private void PlayIntro()
    {
        if (_introDone) return;
        _introDone = true;

        if (!Motion.Enabled)
        {
            Opacity = 1;
            return;
        }

        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = Motion.EaseOut
        });

        Motion.Entrance(TopBar, 0.05, 10);
        Motion.Entrance(Sidebar, 0.14, 12);

        // Свет логотипа набирает интенсивность
        LogoGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0, 0.45,
            TimeSpan.FromMilliseconds(900))
        {
            BeginTime = TimeSpan.FromMilliseconds(250),
            EasingFunction = Motion.EaseOut
        });
    }

    // ---------------------------------------------------------------- Навигация

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var page = button.Tag?.ToString();
        if (string.IsNullOrEmpty(page)) return;

        SetActiveNavButton(button);
        LoadPage(page);
    }

    private Button? FindNavButton(string page) =>
        SidebarPanel.Children.OfType<Button>()
            .FirstOrDefault(b => Equals(b.Tag?.ToString(), page));

    /// <summary>
    /// Активный пункт: стеклянная подложка, светлая граница и градиентный индикатор слева.
    /// DropShadowEffect не используется — сайдбар живёт в ScrollViewer, который обрезает
    /// свечение по краям («невидимая стена» вокруг кнопки).
    /// </summary>
    private void SetActiveNavButton(Button? active)
    {
        foreach (var button in SidebarPanel.Children.OfType<Button>())
        {
            button.Background = Brushes.Transparent;
            button.BorderBrush = Brushes.Transparent;
            button.Effect = null;
            button.FontWeight = FontWeights.Medium;

            if (button.Template?.FindName("NavIndicator", button) is Border indicator)
                indicator.Background = Brushes.Transparent;
        }

        if (active == null) return;

        active.Background = (Brush)FindResource("GlassHover");
        active.BorderBrush = Palette.CardBorderHover;
        active.FontWeight = FontWeights.SemiBold;

        if (active.Template?.FindName("NavIndicator", active) is Border activeIndicator)
        {
            activeIndicator.Background = Palette.AccentGradient();
            if (Motion.Enabled)
            {
                activeIndicator.Opacity = 0;
                activeIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                    TimeSpan.FromMilliseconds(280)) { EasingFunction = Motion.EaseOut });
            }
        }
    }

    private void NavigateTo(string page)
    {
        SetActiveNavButton(FindNavButton(page));
        LoadPage(page);
    }

    private void LoadPage(string pageName)
    {
        var previous = _activePage;
        _activePage = pageName;

        // Направление перехода: вперёд по списку разделов — контент всплывает снизу
        double direction = PageOrder(pageName) >= PageOrder(previous) ? 1 : -1;

        Motion.PageTransition(ContentArea, () => BuildPage(pageName), direction);
    }

    private static int PageOrder(string page) => page switch
    {
        "Home" => 0,
        "Scripts" => 1,
        "Plugins" => 2,
        "Presets" => 3,
        "Overlays" => 4,
        "Sounds" => 5,
        "Library" => 6,
        "Downloads" => 7,
        "Admin" => 8,
        "Settings" => 9,
        _ => 10
    };

    /// <summary>
    /// Разделы, которые показаны в интерфейсе, но ещё не открыты.
    /// Кнопки в сайдбаре отключены, плитки на Home не кликабельны,
    /// а при прямом обращении показывается заглушка «скоро».
    /// </summary>
    private static readonly HashSet<string> ComingSoonPages = new() { "Presets", "Overlays", "Sounds" };

    private static bool IsComingSoon(string page) => ComingSoonPages.Contains(page);

    private static string ComingSoonTitle(string page) => page switch
    {
        "Presets" => "Presets",
        "Overlays" => "Overlays",
        "Sounds" => "Sound Library",
        _ => page
    };

    private void BuildPage(string pageName)
    {
        ContentArea.Children.Clear();

        // Форма карточки админки и страница ресурса живут в отдельных хостах
        // поверх списка: при переходе на любой раздел они закрываются,
        // а контент возвращается. Список перечитает сама страница Admin —
        // иначе две параллельные загрузки встретились бы на флаге занятости
        // и оставили спиннер.
        if (AdminEditorHost.Visibility == Visibility.Visible) CloseAdminEditor(null, reload: false);
        if (ResourceDetailHost.Visibility == Visibility.Visible) CloseResourceDetail(rebuild: false);

        if (IsComingSoon(pageName))
        {
            AddPageHeader(ComingSoonTitle(pageName), "This section is coming soon");
            ContentArea.Children.Add(CreateEmptyState(
                "Coming soon",
                $"The {ComingSoonTitle(pageName).ToLowerInvariant()} section is not available yet. It will appear here in a future update."));
            return;
        }

        switch (pageName)
        {
            case "Home": LoadHomePage(); break;
            case "Scripts": LoadCategoryPage("Scripts", ResourceType.Script); break;
            case "Plugins": LoadCategoryPage("Plugins", ResourceType.Plugin); break;
            case "Library": LoadLibraryPage(); break;
            case "Downloads": LoadDownloadsPage(); break;
            case "Admin": LoadAdminPage(); break;
            case "Settings": LoadSettingsPage(); break;
            default: LoadHomePage(); break;
        }
    }

    private string GetActivePage() => _activePage;

    // ---------------------------------------------------------------- Страницы

    private async void LoadHomePage()
    {
        var hero = CreateHero();
        Motion.Entrance(hero, 0.02);
        ContentArea.Children.Add(hero);

        var resources = await LoadCatalogSafeAsync();
        if (resources == null) return;

        // Страница могла смениться, пока каталог грузился
        if (_activePage != "Home") return;

        var recent = resources.OrderByDescending(r => r.PublishedAt).Take(3).ToList();
        var favoriteIds = _favoritesService.GetAllFavoriteIds();
        var downloads = _loadedService.GetAll();
        var lastDownload = downloads
            .Select(d => resources.FirstOrDefault(r => r.Id == d.ResourceId))
            .LastOrDefault(r => r != null);

        // Continue Creating + Your Library — две стеклянные карточки в ряд
        var duo = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        duo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        duo.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var continueCard = CreateContinueCard(lastDownload);
        Grid.SetColumn(continueCard, 0);
        continueCard.Margin = new Thickness(0, 0, 10, 0);

        var libraryCard = CreateLibraryCard(favoriteIds.Count, downloads.Count);
        Grid.SetColumn(libraryCard, 1);
        libraryCard.Margin = new Thickness(10, 0, 0, 0);

        duo.Children.Add(continueCard);
        duo.Children.Add(libraryCard);
        Motion.Entrance(duo, 0.10);
        ContentArea.Children.Add(duo);

        var quick = CreateQuickAccess();
        Motion.Entrance(quick, 0.16);
        ContentArea.Children.Add(quick);

        var recentHeader = CreateSectionHeader("Recently Added");
        Motion.Entrance(recentHeader, 0.22);
        ContentArea.Children.Add(recentHeader);

        if (recent.Any())
            ContentArea.Children.Add(CreateResourceGrid(recent, startDelay: 0.24));
        else
            ContentArea.Children.Add(CreateEmptyState("Nothing here yet", "New resources will appear in this section"));
    }

    private async void LoadCategoryPage(string title, ResourceType type)
    {
        var page = _activePage;
        AddPageHeader(title, $"Browse {title.ToLowerInvariant()}");

        var resources = await LoadCatalogSafeAsync();
        if (resources == null) return;
        if (_activePage != page) return;

        var filtered = resources.Where(r => r.Type == type).ToList();

        if (filtered.Any())
            ContentArea.Children.Add(CreateResourceGrid(filtered));
        else
            ContentArea.Children.Add(CreateEmptyState($"No {title.ToLowerInvariant()} yet", "This section fills up as resources are added to the catalog"));
    }

    private async void LoadDownloadsPage()
    {
        var page = _activePage;
        AddPageHeader("Downloads", "Resources you have downloaded");

        var downloaded = _loadedService.GetAll();

        if (!downloaded.Any())
        {
            ContentArea.Children.Add(CreateEmptyState("No downloads yet", "Downloaded resources will appear here"));
            return;
        }

        var resources = await LoadCatalogSafeAsync();
        if (resources == null) return;
        if (_activePage != page) return;

        var known = downloaded
            .Select(entry => (entry, resource: resources.FirstOrDefault(r => r.Id == entry.ResourceId)))
            .Where(x => x.resource != null)
            .ToList();

        ContentArea.Children.Add(CreateResourceGrid(
            known.Select(x => x.resource!).ToList(),
            id => known.FirstOrDefault(x => x.resource!.Id == id).entry.FilePath));
    }

    private async void LoadLibraryPage()
    {
        var page = _activePage;
        AddPageHeader("My Library", "Everything you saved and downloaded");

        var favoriteIds = _favoritesService.GetAllFavoriteIds();
        var downloaded = _loadedService.GetAll();

        if (favoriteIds.Count == 0 && downloaded.Count == 0)
        {
            ContentArea.Children.Add(CreateEmptyState(
                "Your library is empty",
                "Save resources with the heart button or download them — they will collect here"));
            return;
        }

        var resources = await LoadCatalogSafeAsync();
        if (resources == null) return;
        if (_activePage != page) return;

        var saved = resources.Where(r => favoriteIds.Contains(r.Id)).ToList();
        if (saved.Any())
        {
            ContentArea.Children.Add(CreateSectionHeader("Saved"));
            ContentArea.Children.Add(CreateResourceGrid(saved));
        }

        var owned = downloaded
            .Select(d => resources.FirstOrDefault(r => r.Id == d.ResourceId))
            .OfType<Resource>()
            .ToList();

        if (owned.Any())
        {
            ContentArea.Children.Add(CreateSectionHeader("Downloaded"));
            ContentArea.Children.Add(CreateResourceGrid(
                owned,
                id => downloaded.FirstOrDefault(d => d.ResourceId == id).FilePath));
        }
    }

    private void LoadSettingsPage()
    {
        AddPageHeader("Settings", "Configure Editor Helper");

        // --- Источник каталога ---
        ContentArea.Children.Add(CreateSectionHeader("Catalog Source"));

        var sourceCard = CreateCard();
        var sourceStack = new StackPanel();

        sourceStack.Children.Add(new TextBlock
        {
            Text = "Where Editor Helper loads resource data from.",
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var localRadio = CreateRadio(!_catalogService.IsRemote,
            "Local Development  —  read the catalog from a local JSON file");
        localRadio.Margin = new Thickness(0, 0, 0, 10);

        var remoteRadio = CreateRadio(_catalogService.IsRemote,
            "Remote Server  —  load the catalog over HTTP from your own server");
        remoteRadio.Margin = new Thickness(0, 0, 0, 14);

        var urlLabel = new TextBlock
        {
            Text = "Server API address",
            FontSize = 12,
            Foreground = Palette.TextMuted,
            Margin = new Thickness(0, 0, 0, 6)
        };

        var urlBox = new TextBox
        {
            Text = _settings.ApiBaseUrl,
            FontSize = 13,
            Padding = new Thickness(12, 9, 12, 9),
            Background = Palette.Glass,
            Foreground = Palette.TextPrimary,
            CaretBrush = Palette.TextPrimary,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            IsEnabled = _catalogService.IsRemote
        };

        remoteRadio.Checked += (_, _) => urlBox.IsEnabled = true;
        localRadio.Checked += (_, _) => urlBox.IsEnabled = false;

        var saveButton = new Button
        {
            Content = "Save and reload catalog",
            Style = (Style)FindResource("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 0)
        };

        saveButton.Click += async (_, _) =>
        {
            _settings.CatalogSource = remoteRadio.IsChecked == true ? "remote" : "local";
            _settings.RequireAuthentication = remoteRadio.IsChecked == true;
            _settings.ApiBaseUrl = urlBox.Text.Trim();
            _settings.Save();

            _catalogService.ApplySettings(_settings);
            UpdateSourceBadge();

            StatusText.Text = "Reloading catalog...";

            var resources = await LoadCatalogSafeAsync();
            if (resources != null)
            {
                StatusText.Text = $"Loaded {resources.Count} resources";
                ShowToast($"Catalog reloaded — {resources.Count} resources", Palette.Accent);
            }

            LoadPage("Settings");
        };

        sourceStack.Children.Add(localRadio);
        sourceStack.Children.Add(remoteRadio);
        sourceStack.Children.Add(urlLabel);
        sourceStack.Children.Add(urlBox);
        sourceStack.Children.Add(saveButton);
        sourceCard.Child = sourceStack;
        ContentArea.Children.Add(sourceCard);

        // --- Внешний вид ---
        ContentArea.Children.Add(CreateSectionHeader("Appearance"));

        var effectsCard = CreateCard();
        var effectsStack = new StackPanel();

        var effectsCheck = new CheckBox
        {
            Content = "Animated background and interface effects",
            IsChecked = _settings.EffectsEnabled,
            Margin = new Thickness(0, 0, 0, 8)
        };

        effectsCheck.Checked += (_, _) => ToggleEffects(true);
        effectsCheck.Unchecked += (_, _) => ToggleEffects(false);

        effectsStack.Children.Add(effectsCheck);
        effectsStack.Children.Add(new TextBlock
        {
            Text = SystemPrefersReducedMotion()
                ? "Windows is set to reduce motion — effects are limited regardless of this switch."
                : "Living space background, page transitions and hover effects. Turn off on slower machines.",
            FontSize = 12,
            Foreground = Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap
        });

        effectsCard.Child = effectsStack;
        ContentArea.Children.Add(effectsCard);

        // --- Папка загрузок ---
        ContentArea.Children.Add(CreateSectionHeader("Downloads"));

        var folderCard = CreateCard();
        var folderStack = new StackPanel();

        folderStack.Children.Add(new TextBlock
        {
            Text = "Currently downloading to:",
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 0, 0, 6)
        });

        folderStack.Children.Add(new TextBlock
        {
            Text = new DownloadService(_settings.DownloadFolder).GetDownloadFolder(),
            FontSize = 12,
            Foreground = Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap
        });

        folderCard.Child = folderStack;
        ContentArea.Children.Add(folderCard);

        // --- Обновления ---
        ContentArea.Children.Add(CreateSectionHeader("Updates"));

        var updateCard = CreateCard();
        var updateStack = new StackPanel();

        updateStack.Children.Add(BuildUpdateSection());
        updateCard.Child = updateStack;
        ContentArea.Children.Add(updateCard);

        // --- Язык и версия ---
        ContentArea.Children.Add(CreateSectionHeader("About"));

        var aboutCard = CreateCard();
        var aboutStack = new StackPanel();

        aboutStack.Children.Add(new TextBlock
        {
            Text = "Interface language: English. Russian and Polish are planned for a later stage.",
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        aboutStack.Children.Add(new TextBlock
        {
            Text = $"Editor Helper v{version?.ToString(3) ?? "0.4"}",
            FontSize = 12,
            Foreground = Palette.TextMuted
        });

        aboutCard.Child = aboutStack;
        ContentArea.Children.Add(aboutCard);
    }

    private void ToggleEffects(bool enabled)
    {
        _settings.EffectsEnabled = enabled;
        _settings.Save();
        ApplyEffectsSetting(enabled && !SystemPrefersReducedMotion());
        ShowToast(enabled ? "Effects enabled" : "Effects disabled", Palette.Accent);
    }

    private RadioButton CreateRadio(bool isChecked, string text) => new()
    {
        Content = text,
        IsChecked = isChecked,
        Foreground = Palette.TextPrimary,
        FontSize = 13,
        GroupName = "CatalogSource"
    };

    // ---------------------------------------------------------------- Hero и блоки Home

    /// <summary>
    /// Приветственная область: крупный заголовок слева и космическая
    /// композиция с аватаркой-«светилом» справа.
    /// </summary>
    private Grid CreateHero()
    {
        var hero = new Grid { Margin = new Thickness(0, 24, 0, 40) };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 32, 0)
        };

        text.Children.Add(new TextBlock
        {
            Text = "PREMIUM COSMIC WORKSPACE",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Accent,
            Margin = new Thickness(0, 0, 0, 16)
        });

        text.Children.Add(new TextBlock
        {
            Text = "Your creative universe.",
            FontSize = 44,
            FontWeight = FontWeights.Light,
            Foreground = Palette.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        text.Children.Add(new TextBlock
        {
            Text = "Everything you need to create something extraordinary.",
            FontSize = 15,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Margin = new Thickness(0, 0, 0, 28)
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };

        var exploreButton = new Button
        {
            Content = "Explore library",
            Style = (Style)FindResource("PrimaryButton")
        };
        exploreButton.Click += (_, _) => NavigateTo("Scripts");

        var libraryButton = new Button
        {
            Content = "My Library",
            Style = (Style)FindResource("GlassButton"),
            Margin = new Thickness(12, 0, 0, 0)
        };
        libraryButton.Click += (_, _) => NavigateTo("Library");

        actions.Children.Add(exploreButton);
        actions.Children.Add(libraryButton);
        text.Children.Add(actions);

        Grid.SetColumn(text, 0);
        hero.Children.Add(text);

        var composition = CreateCosmicComposition(300);
        Grid.SetColumn(composition, 1);
        hero.Children.Add(composition);

        return hero;
    }

    /// <summary>
    /// Космическая композиция: аватарка-«светило» с холодным свечением
    /// в центре медленно вращающихся орбит.
    /// </summary>
    private Grid CreateCosmicComposition(double size)
    {
        var composition = new Grid
        {
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center
        };

        int orbitIndex = 0;
        foreach (var ratio in new[] { 1.0, 0.78, 0.56 })
        {
            var ring = new Ellipse
            {
                Width = size * ratio,
                Height = size * ratio,
                Stroke = orbitIndex == 1 ? Palette.CardBorderHover : Palette.OrbitLine,
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                StrokeDashArray = new DoubleCollection { 6, 4 },
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            if (Motion.Enabled)
            {
                var spin = new RotateTransform(0);
                ring.RenderTransform = spin;
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(
                    orbitIndex % 2 == 0 ? 0 : 360,
                    orbitIndex % 2 == 0 ? 360 : 0,
                    TimeSpan.FromSeconds(90 + orbitIndex * 30))
                { RepeatBehavior = RepeatBehavior.Forever });
            }

            composition.Children.Add(ring);
            orbitIndex++;
        }

        double avatar = size * 0.34;

        // Свечение живёт отдельным слоем ЗА аватаркой. DropShadowEffect на родителе
        // растеризовал бы поддерево и лишил центр резкости, поэтому ореол нарисован
        // радиальным градиентом — он независим от маски прозрачности изображения.
        var halo = new Ellipse
        {
            Width = avatar * 2.1,
            Height = avatar * 2.1,
            Fill = HaloBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Opacity = 0.9
        };
        composition.Children.Add(halo);

        // Никакой жёсткой круглой рамки: маску растворяет края сама,
        // а контур поверх неё вернул бы эффект «вырезанного кружка».
        var avatarBorder = new Border
        {
            Width = avatar,
            Height = avatar,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new AvatarImage { Source = LoadAvatar() }
        };
        composition.Children.Add(avatarBorder);

        if (Motion.Enabled)
        {
            // Мягкое «дыхание» ореола вокруг аватарки — независимо от маски
            halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 0.95,
                TimeSpan.FromSeconds(3.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseInOut
            });

            // Едва заметное «дыхание» масштаба в такт свечению
            var breathe = new ScaleTransform(1, 1, avatar / 2, avatar / 2);
            avatarBorder.RenderTransform = breathe;
            breathe.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.025,
                TimeSpan.FromSeconds(3.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseInOut
            });
            breathe.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.025,
                TimeSpan.FromSeconds(3.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.EaseInOut
            });
        }

        // Орбитальные точки — по одной на каждой орбите, холодный циан
        var points = new[] { (size * 1.0, 0.0), (size * 0.78, 2.3), (size * 0.56, 4.1) };
        foreach (var (diameter, angle) in points)
        {
            double x = size / 2 + Math.Cos(angle) * diameter / 2;
            double y = size / 2 + Math.Sin(angle) * diameter / 2;

            var dot = new Ellipse
            {
                Width = 5,
                Height = 5,
                Fill = Palette.Accent,
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(x - 2.5, y - 2.5, 0, 0),
                Effect = Palette.Glow(0.55, 10)
            };
            composition.Children.Add(dot);
        }

        return composition;
    }

    private static BitmapImage? _avatarCache;

    /// <summary>
    /// Мягкий ореол за аватаркой: радиальный градиент от белого к прозрачному.
    /// Заменяет DropShadowEffect, который растеризовал бы изображение и лишил
    /// центр резкости.
    /// </summary>
    private static Brush HaloBrush()
    {
        var halo = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF), 0.00));
        halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 0.45));
        halo.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.00));
        halo.Freeze();
        return halo;
    }

    /// <summary>Аватарка вшита в сборку как ресурс, с рабочего стола не читается.</summary>
    private static BitmapImage? LoadAvatar()
    {
        if (_avatarCache != null) return _avatarCache;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Resources/avatar.png");
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            _avatarCache = image;
            return image;
        }
        catch
        {
            return null;
        }
    }

    private Border CreateContinueCard(Resource? last)
    {
        var card = CreateGlassCard();
        card.MinHeight = 150;
        var stack = new StackPanel();

        stack.Children.Add(CreateIconBadge(""));

        stack.Children.Add(new TextBlock
        {
            Text = "Continue Creating",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            Margin = new Thickness(0, 16, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = last != null
                ? $"Pick up where you left off — {last.Title}"
                : "Nothing in progress yet. Your last opened resource will appear here.",
            FontSize = 13,
            Foreground = last != null ? Palette.TextSecondary : Palette.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var resume = new Button
        {
            Content = last != null ? "Resume" : "Browse catalog",
            Style = (Style)FindResource("GlassButtonSmall"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        resume.Click += (_, _) =>
        {
            if (last != null) OpenDetails(last);
            else NavigateTo("Scripts");
        };

        stack.Children.Add(resume);
        card.Child = stack;
        Motion.AttachCardInteraction(card, Palette.CardBorderHover, Palette.GlassBorder);
        return card;
    }

    private Border CreateLibraryCard(int savedCount, int downloadCount)
    {
        var card = CreateGlassCard();
        card.MinHeight = 150;
        var stack = new StackPanel();

        stack.Children.Add(CreateIconBadge(""));

        stack.Children.Add(new TextBlock
        {
            Text = "Your Library",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            Margin = new Thickness(0, 16, 0, 6)
        });

        stack.Children.Add(new TextBlock
        {
            Text = $"{savedCount} saved · {downloadCount} downloaded",
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var open = new Button
        {
            Content = "Open library",
            Style = (Style)FindResource("GlassButtonSmall"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        open.Click += (_, _) => NavigateTo("Library");

        stack.Children.Add(open);
        card.Child = stack;
        Motion.AttachCardInteraction(card, Palette.CardBorderHover, Palette.GlassBorder);
        return card;
    }

    /// <summary>Быстрый доступ к основным разделам — ряд стеклянных плиток.</summary>
    private StackPanel CreateQuickAccess()
    {
        var section = new StackPanel { Margin = new Thickness(0, 24, 0, 0) };
        section.Children.Add(CreateSectionHeader("Quick Access"));

        var items = new (string Page, string Glyph, string Label)[]
        {
            ("Scripts", "", "Scripts"),
            ("Plugins", "", "Plugins"),
            ("Presets", "", "Presets"),
            ("Overlays", "", "Overlays"),
            ("Sounds", "", "Sound")
        };

        var grid = new Grid();
        for (int i = 0; i < items.Length; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < items.Length; i++)
        {
            var (page, glyph, label) = items[i];
            bool soon = IsComingSoon(page);

            var tile = CreateGlassCard();
            tile.Margin = new Thickness(0, 0, i < items.Length - 1 ? 14 : 0, 0);
            tile.Cursor = soon ? Cursors.Arrow : Cursors.Hand;

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = soon ? Palette.TextMuted : Palette.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            });
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                Foreground = soon ? Palette.TextMuted : Palette.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // Разделы «скоро» получают ту же пилюлю, что и в сайдбаре,
            // чтобы статус был виден сразу, а не только в подсказке при наведении
            if (soon)
                stack.Children.Add(CreateSoonBadge());

            tile.Child = stack;

            if (soon)
            {
                // Раздел ещё не открыт: плитка не кликабельна и не подсвечивается.
                // Затемнение умеренное: бейдж «Soon» живёт внутри плитки и должен
                // оставаться читаемым, иначе статус снова не разглядеть.
                tile.Opacity = 0.72;
                tile.ToolTip = "Coming soon";
            }
            else
            {
                Motion.AttachCardInteraction(tile, Palette.CardBorderHover, Palette.GlassBorder);

                string target = page;
                tile.MouseLeftButtonUp += (_, _) => NavigateTo(target);
            }

            Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }

        section.Children.Add(grid);
        return section;
    }

    /// <summary>
    /// Пилюля «Soon» для разделов, которые ещё не открыты. Тот же визуальный
    /// элемент, что и в боковой навигации, чтобы статус читался одинаково везде.
    /// </summary>
    private Border CreateSoonBadge()
    {
        return new Border
        {
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = "Soon",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
    }

    private Border CreateIconBadge(string glyph)
    {
        return new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = Palette.Glass,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = Palette.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    // ---------------------------------------------------------------- Каталог, загрузка и ошибки

    /// <summary>
    /// Загружает ресурсы, показывая индикатор загрузки вместо пустого экрана.
    /// Если источник недоступен — понятное сообщение об ошибке. Возвращает null при ошибке.
    /// </summary>
    private async Task<List<Resource>?> LoadCatalogSafeAsync()
    {
        var spinner = CreateLoadingState();
        ContentArea.Children.Add(spinner);

        try
        {
            var resources = await _catalogService.GetAllResourcesAsync();
            ContentArea.Children.Remove(spinner);

            StatusText.Text = _catalogService.IsRemote
                ? $"Connected to {_settings.ApiBaseUrl}"
                : $"Local Development — {resources.Count} resources";
            return resources;
        }
        catch (CatalogUnavailableException ex)
        {
            ContentArea.Children.Remove(spinner);
            StatusText.Text = "Catalog unavailable";
            ContentArea.Children.Add(CreateErrorState(
                "Cannot load the catalog",
                ex.Message,
                _catalogService.IsRemote ? "Switch back to Local Development in Settings" : null));
            return null;
        }
        catch (Exception ex)
        {
            ContentArea.Children.Remove(spinner);
            StatusText.Text = "Catalog error";
            ContentArea.Children.Add(CreateErrorState("Something went wrong", ex.Message, null));
            return null;
        }
    }

    /// <summary>Аккуратный индикатор загрузки каталога (без выдуманных процентов).</summary>
    private StackPanel CreateLoadingState()
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(0, 48, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var ring = new Ellipse
        {
            Width = 34,
            Height = 34,
            Stroke = Palette.AccentSoft,
            StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 3.2, 2.2 },
            StrokeDashCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };

        if (Motion.Enabled)
        {
            var spin = new RotateTransform(0);
            ring.RenderTransform = spin;
            spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360,
                TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        stack.Children.Add(ring);
        stack.Children.Add(new TextBlock
        {
            Text = "Loading catalog…",
            FontSize = 13,
            Foreground = Palette.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        });

        return stack;
    }

    private void UpdateSourceBadge()
    {
        SourceBadgeText.Text = _catalogService.GetSourceName();
        SourceBadgeText.Foreground = _catalogService.IsRemote ? Palette.Accent : Palette.TextSecondary;
    }

    // ---------------------------------------------------------------- Уведомления

    /// <summary>
    /// Всплывающее уведомление справа внизу: появляется, держится и аккуратно исчезает.
    /// Не перекрывает контент — живёт в отдельном хосте поверх окна.
    /// </summary>
    private void ShowToast(string message, Brush? accent = null)
    {
        var toast = new Border
        {
            Background = Palette.BgElevated,
            BorderBrush = Palette.GlassBorderBright,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10),
            MaxWidth = 340,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
            Effect = Palette.CardShadowEffect()
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = "",  // MDL2: Info
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = accent ?? Palette.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = Palette.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });

        toast.Child = panel;
        ToastHost.Children.Add(toast);

        if (!Motion.Enabled)
        {
            var plainTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            plainTimer.Tick += (_, _) =>
            {
                plainTimer.Stop();
                ToastHost.Children.Remove(toast);
            };
            plainTimer.Start();
            return;
        }

        var slide = new TranslateTransform(40, 0);
        toast.RenderTransform = slide;
        toast.Opacity = 0;

        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0,
            TimeSpan.FromMilliseconds(320)) { EasingFunction = Motion.EaseOut });
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
            TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.EaseOut });

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.2)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = Motion.EaseOut
            };
            fadeOut.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    // ---------------------------------------------------------------- Поиск

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
        else if (e.Key == Key.Escape) ClearSearch();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Событие приходит и во время InitializeComponent, когда кнопка ещё не создана
        if (SearchClearButton == null) return;

        bool hasText = !string.IsNullOrWhiteSpace(SearchBox.Text) && SearchBox.Text != SearchPlaceholder;
        SearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e) => ClearSearch();

    private void ClearSearch()
    {
        SearchBox.Text = SearchPlaceholder;
        SearchBox.Foreground = Palette.TextMuted;
        SearchClearButton.Visibility = Visibility.Collapsed;
        NavigateTo(_activePage == "Home" ? "Home" : GetActivePage());
    }

    private async void RunSearch()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query) || query == SearchPlaceholder) return;

        ContentArea.Children.Clear();
        AddPageHeader($"Search results for “{query}”", "Press Esc to clear");

        try
        {
            var results = await _catalogService.SearchAsync(query);

            if (results.Any())
                ContentArea.Children.Add(CreateResourceGrid(results));
            else
                ContentArea.Children.Add(CreateEmptyState("No resources found", $"Nothing matches “{query}”"));
        }
        catch (CatalogUnavailableException ex)
        {
            ContentArea.Children.Add(CreateErrorState("Search failed", ex.Message, null));
        }
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text == SearchPlaceholder)
        {
            SearchBox.Text = "";
            SearchBox.Foreground = Palette.TextPrimary;
        }

        if (Motion.Enabled)
        {
            SearchFrame.BorderBrush = Palette.CardBorderHover;
            SearchFrame.Effect = Palette.Glow(0.16, 18);
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchBox.Text = SearchPlaceholder;
            SearchBox.Foreground = Palette.TextMuted;
            SearchClearButton.Visibility = Visibility.Collapsed;
        }

        SearchFrame.BorderBrush = (Brush)FindResource("GlassBorder");
        SearchFrame.Effect = null;
    }

    // ---------------------------------------------------------------- Карточки

    private Grid CreateResourceGrid(List<Resource> resources, Func<string, string?>? pathFor = null, double startDelay = 0)
    {
        const double gap = 20;

        // Отрицательный правый отступ компенсирует margin последней колонки:
        // правый край сетки совпадает с заголовком страницы, а карточки во всех
        // колонках одинаковой ширины.
        var grid = new Grid { Margin = new Thickness(0, 0, -gap, 24) };

        for (int i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0, col = 0, index = 0;
        foreach (var resource in resources)
        {
            if (col == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var card = CreateResourceCard(resource, pathFor?.Invoke(resource.Id) ?? _loadedService.GetFilePath(resource.Id));
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            grid.Children.Add(card);

            // Карточки «всплывают» волной, но не дольше первых двенадцати
            Motion.Entrance(card, startDelay + Math.Min(index, 12) * 0.045);
            index++;

            if (++col >= 3) { col = 0; row++; }
        }

        return grid;
    }

    /// <summary>
    /// Карточка ресурса в сетке каталога. Общий построитель живёт в
    /// <see cref="ResourceCardView"/>: его же использует предпросмотр в
    /// админ-панели, чтобы владелец видел ровно то, что получат пользователи.
    /// </summary>
    private FrameworkElement CreateResourceCard(Resource resource, string? downloadedPath) =>
        ResourceCardView.Create(resource, downloadedPath, () => OpenDetails(resource));

    /// <summary>
    /// Страница карточки открывается внутри основного окна — отдельным хостом
    /// поверх каталога, как форма админки. Кнопка «назад» возвращает список.
    /// </summary>
    private void OpenDetails(Resource resource)
    {
        var detail = new ResourceDetailView(
            resource, _settings, _loadedService, _favoritesService,
            () => CloseResourceDetail());

        ResourceDetailHost.Children.Clear();
        ResourceDetailHost.Children.Add(detail);
        ContentScroll.Visibility = Visibility.Collapsed;
        ResourceDetailHost.Visibility = Visibility.Visible;
        ContentScroll.ScrollToTop();
        Motion.Entrance(detail, 0, 10);
    }

    /// <param name="rebuild">Перечитать ли каталог: статус загрузки мог измениться.</param>
    private void CloseResourceDetail(bool rebuild = true)
    {
        if (ResourceDetailHost.Visibility != Visibility.Visible) return;

        ResourceDetailHost.Children.Clear();
        ResourceDetailHost.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Visible;

        // Без анимированного перехода: пользователь не менял раздел.
        if (rebuild) BuildPage(GetActivePage());
    }

    // ---------------------------------------------------------------- UI-помощники

    private void AddPageHeader(string title, string description)
    {
        ContentArea.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 32,
            FontWeight = FontWeights.Light,
            Foreground = Palette.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8)
        });

        ContentArea.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13.5,
            Foreground = Palette.TextSecondary,
            Margin = new Thickness(0, 0, 0, 30)
        });
    }

    private TextBlock CreateSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        Foreground = Palette.TextPrimary,
        Margin = new Thickness(0, 12, 0, 14)
    };

    /// <summary>Стеклянная карточка-контейнер: полупрозрачная поверхность, тонкая граница.</summary>
    private Border CreateGlassCard()
    {
        return new Border
        {
            Background = Palette.CardSurface,
            BorderBrush = Palette.GlassBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22),
            Margin = new Thickness(0, 0, 0, 16)
        };
    }

    private Border CreateCard() => CreateGlassCard();

    private Border CreateBadge(string text, Brush? foreground = null)
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

    private StackPanel CreateEmptyState(string title, string message)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(0, 64, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = "✦",
            FontSize = 22,
            Foreground = Palette.AccentSoft,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        });

        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13.5,
            Foreground = Palette.TextMuted,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 460,
            TextWrapping = TextWrapping.Wrap
        });

        Motion.Entrance(stack, 0.05);
        return stack;
    }

    private Border CreateErrorState(string title, string message, string? hint)
    {
        var card = new Border
        {
            Background = Palette.CardSurface,
            BorderBrush = Palette.Danger,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24),
            Margin = new Thickness(0, 24, 0, 0),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var stack = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = Palette.Danger,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.TextPrimary,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(header);

        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = Palette.TextSecondary,
            TextWrapping = TextWrapping.Wrap
        });

        if (hint != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 12.5,
                Foreground = Palette.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            });
        }

        card.Child = stack;
        Motion.Entrance(card, 0.05);
        return card;
    }
}
