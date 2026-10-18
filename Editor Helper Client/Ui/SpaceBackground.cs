using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace EditorHelper.Ui;

/// <summary>
/// Живой космический фон приложения. Три слоя частиц с разной скоростью
/// (дальний, средний, ближний), тонкая сеть связей между близкими частицами,
/// медленно дрейфующие туманности и мягкий параллакс за курсором.
///
/// Всё рисуется одним вызовом OnRender — отдельного элемента на частицу нет,
/// поэтому нагрузка на дерево визуальных элементов нулевая. Кисти и перья
/// создаются один раз и переиспользуются, в кадровом цикле аллокаций нет.
/// Анимация останавливается, когда окно свёрнуто или элемент невидим.
/// </summary>
public sealed class SpaceBackground : FrameworkElement
{
    // ------------------------------------------------------------ Слои частиц

    private struct Particle
    {
        public double X, Y;
        public double Vx, Vy;
        public double Radius;
        public double BaseOpacity;
        public double TwinklePhase;
        public double TwinkleSpeed;
        public double Parallax;   // насколько слой смещается за курсором
        public bool Network;      // участвует ли в линиях связи
    }

    private Particle[] _particles = Array.Empty<Particle>();
    private const int NetworkLimit = 64;   // сколько частиц соединяем линиями

    private const double LinkDistance = 118;
    private const int MaxLinksPerFrame = 150;

    // ------------------------------------------------------------ Туманности

    private struct Nebula
    {
        public double CxRatio, CyRatio;
        public double RadiusRatio;
        public double DriftX, DriftY;      // амплитуда дрейфа в долях размера
        public double Period;              // секунд на полный цикл
        public double Phase;
        public Brush Brush;
    }

    private Nebula[] _nebulas = Array.Empty<Nebula>();

    // ------------------------------------------------------------ Мерцающие звёзды

    private struct Sparkle
    {
        public double X, Y;
        public double Size;
        public double Phase;
        public double Speed;
        public double Parallax;
    }

    private Sparkle[] _sparkles = Array.Empty<Sparkle>();

    // ------------------------------------------------------------ Кэш кистей

    // Прозрачность частиц и линий квантуем, чтобы не создавать кисть в каждом кадре
    private const int OpacitySteps = 14;
    private static readonly Brush[] ParticleBrushes = BuildOpacityBrushes(Colors.White);

    private static readonly Pen[] LinkPens = BuildLinkPens();
    private static readonly Pen SparklePen = BuildSparklePen();

    // ------------------------------------------------------------ Кадровый цикл

    private readonly Stopwatch _clock = new();
    private TimeSpan _lastFrame;
    private bool _running;

    private double _parallaxX, _parallaxY;         // текущее смещение (сглаженное)
    private double _parallaxTargetX, _parallaxTargetY;

    public SpaceBackground()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Start();
            else Stop();
        };
        SizeChanged += (_, _) => Build();
    }

    /// <summary>Точка, за которой следует параллакс: координаты курсора в окне.</summary>
    public void UpdateParallax(double mouseX, double mouseY, double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        _parallaxTargetX = (mouseX / width - 0.5) * 2;   // -1..1
        _parallaxTargetY = (mouseY / height - 0.5) * 2;
    }

    public void Start()
    {
        if (_running || !Motion.Enabled) return;

        if (_particles.Length == 0) Build();
        if (!_clock.IsRunning) _clock.Start();

        _lastFrame = _clock.Elapsed;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Фон перестроен под новые размеры; при выключенных эффектах рисуем один статичный кадр.</summary>
    private void Build()
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        BuildParticles(w, h);
        BuildNebulas();
        BuildSparkles(w, h);

        if (!Motion.Enabled)
        {
            // Без анимации показываем фон один раз — интерфейс не остаётся «мёртвым»
            InvalidateVisual();
            return;
        }

        if (!_clock.IsRunning) _clock.Start();
        InvalidateVisual();
    }

    private void BuildParticles(double w, double h)
    {
        double area = w * h;
        var random = new Random(20260109);

        // Дальний слой: много мелких тусклых звёзд, почти неподвижны
        int far = Clamp((int)(area / 9000), 90, 260);
        // Средний слой: светящиеся частицы, медленный дрейф
        int mid = Clamp((int)(area / 26000), 34, 110);
        // Ближний слой: редкие крупные частицы с мягким свечением
        int near = Clamp((int)(area / 95000), 9, 30);

        _particles = new Particle[far + mid + near];
        int i = 0;

        for (int k = 0; k < far; k++)
            _particles[i++] = NewParticle(random, w, h, 0.5, 1.1, 0.06, 0.22, 2, 5, 0.25, false);

        for (int k = 0; k < mid; k++)
            _particles[i++] = NewParticle(random, w, h, 0.9, 1.9, 0.14, 0.42, 7, 15, 0.6, true);

        for (int k = 0; k < near; k++)
            _particles[i++] = NewParticle(random, w, h, 1.8, 3.4, 0.24, 0.60, 16, 30, 1.0, false);

        // Линии связи строим среди первых NetworkLimit частиц среднего слоя,
        // поэтому переставляем их в начало массива.
        MoveNetworkToFront();
    }

    private static Particle NewParticle(Random random, double w, double h,
        double minR, double maxR, double minO, double maxO,
        double minSpeed, double maxSpeed, double parallax, bool network)
    {
        double speed = minSpeed + random.NextDouble() * (maxSpeed - minSpeed);
        double angle = random.NextDouble() * Math.PI * 2;

        return new Particle
        {
            X = random.NextDouble() * w,
            Y = random.NextDouble() * h,
            Vx = Math.Cos(angle) * speed,
            Vy = Math.Sin(angle) * speed,
            Radius = minR + random.NextDouble() * (maxR - minR),
            BaseOpacity = minO + random.NextDouble() * (maxO - minO),
            TwinklePhase = random.NextDouble() * Math.PI * 2,
            TwinkleSpeed = 0.25 + random.NextDouble() * 0.85,
            Parallax = parallax,
            Network = network
        };
    }

    private void MoveNetworkToFront()
    {
        int write = 0;
        for (int read = 0; read < _particles.Length && write < NetworkLimit; read++)
        {
            if (!_particles[read].Network) continue;
            if (read != write)
            {
                var tmp = _particles[write];
                _particles[write] = _particles[read];
                _particles[read] = tmp;
            }
            write++;
        }
    }

    private void BuildNebulas()
    {
        _nebulas = new[]
        {
            new Nebula
            {
                CxRatio = 0.80, CyRatio = 0.14, RadiusRatio = 0.72,
                DriftX = 0.05, DriftY = 0.04, Period = 58, Phase = 0.0,
                Brush = NebulaBrush("#FFFFFF", 0.055)
            },
            new Nebula
            {
                CxRatio = 0.16, CyRatio = 0.88, RadiusRatio = 0.68,
                DriftX = 0.045, DriftY = 0.05, Period = 71, Phase = 1.9,
                Brush = NebulaBrush("#D8D8D8", 0.045)
            },
            new Nebula
            {
                CxRatio = 0.50, CyRatio = 0.50, RadiusRatio = 0.95,
                DriftX = 0.03, DriftY = 0.03, Period = 94, Phase = 3.4,
                Brush = NebulaBrush("#9A9A9A", 0.03)
            }
        };
    }

    private void BuildSparkles(double w, double h)
    {
        var random = new Random(777);
        _sparkles = new Sparkle[9];
        for (int i = 0; i < _sparkles.Length; i++)
        {
            _sparkles[i] = new Sparkle
            {
                X = random.NextDouble() * w,
                Y = random.NextDouble() * h,
                Size = 3.0 + random.NextDouble() * 4.0,
                Phase = random.NextDouble() * Math.PI * 2,
                Speed = 0.30 + random.NextDouble() * 0.5,
                Parallax = 0.6 + random.NextDouble() * 0.5
            };
        }
    }

    // ------------------------------------------------------------ Кадр

    private void OnFrame(object? sender, EventArgs e)
    {
        TimeSpan now = _clock.Elapsed;
        // dt ограничиваем, чтобы после паузы частицы не «телепортировались»
        double dt = Math.Min((now - _lastFrame).TotalSeconds, 0.05);
        _lastFrame = now;

        if (dt <= 0) return;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Параллакс: плавно догоняем курсор, без резких прыжков
        double smooth = 1 - Math.Exp(-dt * 2.2);
        _parallaxX += (_parallaxTargetX - _parallaxX) * smooth;
        _parallaxY += (_parallaxTargetY - _parallaxY) * smooth;

        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            p.X += p.Vx * dt;
            p.Y += p.Vy * dt;
            p.TwinklePhase += p.TwinkleSpeed * dt;

            // Мягкий выход за край — частица появляется с противоположной стороны
            double margin = p.Radius * 3 + 4;
            if (p.X < -margin) p.X = w + margin;
            else if (p.X > w + margin) p.X = -margin;
            if (p.Y < -margin) p.Y = h + margin;
            else if (p.Y > h + margin) p.Y = -margin;
        }

        InvalidateVisual();
    }

    // ------------------------------------------------------------ Отрисовка

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double time = _clock.Elapsed.TotalSeconds;

        DrawNebulas(dc, w, h, time);
        DrawParticles(dc);
        DrawLinks(dc);
        DrawSparkles(dc, time);
    }

    private void DrawNebulas(DrawingContext dc, double w, double h, double time)
    {
        foreach (var nebula in _nebulas)
        {
            double t = time / nebula.Period * Math.PI * 2 + nebula.Phase;

            double cx = w * nebula.CxRatio + Math.Cos(t) * w * nebula.DriftX;
            double cy = h * nebula.CyRatio + Math.Sin(t * 0.8) * h * nebula.DriftY;
            double radius = Math.Min(w, h) * nebula.RadiusRatio * (1 + Math.Sin(t * 0.6) * 0.08);

            // Едва заметное дыхание яркости
            double opacity = 0.82 + Math.Sin(t * 1.3) * 0.18;

            // Туманности живут на самом дальнем плане и почти не реагируют на курсор
            cx += _parallaxX * 10;
            cy += _parallaxY * 8;

            dc.PushOpacity(opacity);
            dc.DrawEllipse(nebula.Brush, null, new Point(cx, cy), radius, radius * 0.86);
            dc.Pop();
        }
    }

    private void DrawParticles(DrawingContext dc)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];

            double twinkle = 0.72 + Math.Sin(p.TwinklePhase) * 0.28;
            double opacity = Clamp01(p.BaseOpacity * twinkle);

            double x = p.X + _parallaxX * 16 * p.Parallax;
            double y = p.Y + _parallaxY * 12 * p.Parallax;

            dc.DrawEllipse(PickBrush(ParticleBrushes, opacity), null, new Point(x, y), p.Radius, p.Radius);
        }
    }

    private void DrawLinks(DrawingContext dc)
    {
        int count = Math.Min(_particles.Length, NetworkLimit);
        int drawn = 0;

        for (int i = 0; i < count && drawn < MaxLinksPerFrame; i++)
        {
            ref Particle a = ref _particles[i];
            double ax = a.X + _parallaxX * 16 * a.Parallax;
            double ay = a.Y + _parallaxY * 12 * a.Parallax;

            for (int j = i + 1; j < count && drawn < MaxLinksPerFrame; j++)
            {
                ref Particle b = ref _particles[j];
                double bx = b.X + _parallaxX * 16 * b.Parallax;
                double by = b.Y + _parallaxY * 12 * b.Parallax;

                double dx = ax - bx;
                double dy = ay - by;
                double distSq = dx * dx + dy * dy;
                if (distSq > LinkDistance * LinkDistance) continue;

                // Линия тем прозрачнее, чем дальше частицы друг от друга
                double strength = 1 - Math.Sqrt(distSq) / LinkDistance;
                double opacity = strength * strength * 0.34;

                dc.DrawLine(PickPen(LinkPens, opacity), new Point(ax, ay), new Point(bx, by));
                drawn++;
            }
        }
    }

    private void DrawSparkles(DrawingContext dc, double time)
    {
        foreach (var s in _sparkles)
        {
            double pulse = Math.Sin(time * s.Speed + s.Phase);
            double opacity = 0.10 + Math.Max(0, pulse) * 0.34;
            double len = s.Size * (0.7 + Math.Max(0, pulse) * 0.5);

            double x = s.X + _parallaxX * 20 * s.Parallax;
            double y = s.Y + _parallaxY * 15 * s.Parallax;

            dc.PushOpacity(opacity);
            dc.DrawLine(SparklePen, new Point(x - len, y), new Point(x + len, y));
            dc.DrawLine(SparklePen, new Point(x, y - len), new Point(x, y + len));
            dc.Pop();
        }
    }

    // ------------------------------------------------------------ Вспомогательное

    private static Brush PickBrush(Brush[] ramp, double opacity)
    {
        int index = (int)(Clamp01(opacity) * (ramp.Length - 1) + 0.5);
        return ramp[index];
    }

    private static Pen PickPen(Pen[] ramp, double opacity)
    {
        int index = (int)(Clamp01(opacity) * (ramp.Length - 1) + 0.5);
        return ramp[index];
    }

    private static Brush[] BuildOpacityBrushes(Color color)
    {
        var ramp = new Brush[OpacitySteps];
        for (int i = 0; i < OpacitySteps; i++)
        {
            double opacity = (double)i / (OpacitySteps - 1);
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)(opacity * 255), color.R, color.G, color.B));
            brush.Freeze();
            ramp[i] = brush;
        }
        return ramp;
    }

    private static Pen[] BuildLinkPens()
    {
        var linkColor = Colors.White;
        var ramp = new Pen[OpacitySteps];
        for (int i = 0; i < OpacitySteps; i++)
        {
            double opacity = (double)i / (OpacitySteps - 1);
            var pen = new Pen(new SolidColorBrush(WithAlpha(linkColor, opacity)), 0.6);
            pen.Freeze();
            ramp[i] = pen;
        }
        return ramp;
    }

    private static Pen BuildSparklePen()
    {
        var pen = new Pen(Brushes.White, 0.8);
        pen.Freeze();
        return pen;
    }

    private static Brush NebulaBrush(string hex, double peakOpacity)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, peakOpacity), 0.0));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, peakOpacity * 0.45), 0.42));
        brush.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1.0));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)(Clamp01(a) * 255), c.R, c.G, c.B);

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
