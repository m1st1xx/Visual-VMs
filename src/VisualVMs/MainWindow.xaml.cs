using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace VisualVMs;

public partial class MainWindow : Window
{
    enum Mode { Idle, PickSource, Cable, PickConnection, PickDelete }

    static readonly object InternetKey = new();
    static readonly string LastPath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VisualVMs", "last.txt");
    const string GuiWarning = "VMware Workstation открыт — закройте его, иначе имя сегмента может пропасть из VMware (сеть будет работать)";

    Scheme scheme = new();
    string? path;
    bool dirty, busy, polling;
    List<Edge> edges = new();
    Dictionary<string, string> segments = new();
    HashSet<string> running = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<VmNode, Ellipse> dots = new();

    Mode mode;
    object? cableFrom, drag;
    Line? rubber;
    Point dragStart, menuPoint;
    Vector dragOffset;
    bool dragMoved;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var last = File.Exists(LastPath) ? File.ReadAllText(LastPath).Trim() : "";
            if (File.Exists(last)) OpenScheme(last); else Resync();
            UpdateTitle();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += async (_, _) => await Poll();
            timer.Start();
            _ = Poll();
        };
    }

    // ---- state / drawing ----

    void UpdateTitle() => Title = $"Visual VMs — {(path == null ? "Новая схема" : IOPath.GetFileName(path))}{(dirty ? " *" : "")}";

    void MarkDirty() { dirty = true; UpdateTitle(); }

    /// <summary>Re-reads every vmx and preferences.ini, rebuilds edges, redraws. vmx files are the only source of truth for links.</summary>
    void Resync()
    {
        foreach (var vm in scheme.Vms)
        {
            vm.Missing = !File.Exists(vm.Vmx);
            vm.Name = IOPath.GetFileNameWithoutExtension(vm.Vmx);
            vm.Nics = new();
            if (vm.Missing) continue;
            try
            {
                var k = KvFile.Load(vm.Vmx);
                vm.Name = Vmware.DisplayName(vm.Vmx, k);
                vm.Nics = k.Nics();
            }
            catch (Exception ex) { Status.Text = $"{vm.Name}: {ex.Message}"; }
        }
        try { segments = Vmware.Segments(); }
        catch (Exception ex) { segments = new(); Status.Text = "preferences.ini: " + ex.Message; }
        int hubs = scheme.Hubs.Count;
        edges = Topology.Infer(scheme.Vms, scheme.Hubs, segments);
        if (scheme.Hubs.Count != hubs) MarkDirty();
        Redraw();
    }

    Point Center(object node) => node switch
    {
        VmNode v => new(v.X, v.Y),
        HubNode h => new(h.X, h.Y),
        _ => new(scheme.Internet.X, scheme.Internet.Y),
    };

    void SetCenter(object node, Point p)
    {
        p = new(Math.Clamp(p.X, 45, Board.Width - 45), Math.Clamp(p.Y, 25, Board.Height - 60));
        if (node is VmNode v) (v.X, v.Y) = (p.X, p.Y);
        else if (node is HubNode h) (h.X, h.Y) = (p.X, p.Y);
        else scheme.Internet = new(p.X, p.Y);
    }

    static Brush Hex(string c) => (Brush)new BrushConverter().ConvertFromString(c)!;

    void Redraw()
    {
        Board.Children.Clear();
        dots.Clear();
        foreach (var e in edges)
        {
            var a = Center(e.Ends[0].Vm);
            var b = e.Kind switch { EdgeKind.Nat => Center(InternetKey), EdgeKind.Direct => Center(e.Ends[1].Vm), _ => Center(e.Hub!) };
            var color = e.Kind switch { EdgeKind.Direct => "#1565C0", EdgeKind.Hub => "#2E7D32", _ => "#EF6C00" };
            Board.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Hex(color), StrokeThickness = 3, IsHitTestVisible = false });
            // wide transparent twin makes the cable easy to click
            Board.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = Brushes.Transparent, StrokeThickness = 14, Tag = e, Cursor = Cursors.Hand,
                ToolTip = e.Kind == EdgeKind.Nat ? "NAT" : e.Hub?.Name ?? Topology.SegmentName(e.PvnId!, segments),
            });
        }
        foreach (var h in scheme.Hubs)
            Place(new StackPanel
            {
                Width = 90, Background = Brushes.Transparent, Tag = h,
                Children = { new Ellipse { Width = 36, Height = 36, Fill = Hex("#C8E6C9"), Stroke = Hex("#2E7D32"), StrokeThickness = 2 }, Label(h.Name) },
            }, h.X, h.Y, 18);
        Place(Node("Cloud", "#F5F5F5", "Интернет", InternetKey, null), scheme.Internet.X, scheme.Internet.Y, 24);
        foreach (var vm in scheme.Vms)
        {
            var dot = new Ellipse { Width = 10, Height = 10, Stroke = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            dots[vm] = dot;
            var (icon, fill) = vm.Purpose switch { Purpose.Router => ("Router", "#E3F2FD"), Purpose.Server => ("Server", "#E8F5E9"), _ => ("Client", "#FFF3E0") };
            Place(Node(icon, fill, vm.Name, vm, dot), vm.X, vm.Y, 24);
        }
        UpdateDots();
        if (rubber != null) Board.Children.Add(rubber);
    }

    static TextBlock Label(string text) => new() { Text = text, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };

    FrameworkElement Node(string icon, string fill, string name, object tag, Ellipse? dot)
    {
        var g = new Grid { Width = 48, Height = 48 };
        g.Children.Add(new System.Windows.Shapes.Path { Data = (Geometry)FindResource(icon), Stroke = Hex("#333"), StrokeThickness = 2, Fill = Hex(fill) });
        if (dot != null) g.Children.Add(dot);
        return new StackPanel { Width = 90, Background = Brushes.Transparent, Tag = tag, Children = { g, Label(name) } };
    }

    void Place(FrameworkElement el, double x, double y, double halfHeight)
    {
        Canvas.SetLeft(el, x - 45);
        Canvas.SetTop(el, y - halfHeight);
        Board.Children.Add(el);
    }

    void UpdateDots()
    {
        foreach (var (vm, dot) in dots)
        {
            bool on = !vm.Missing && running.Contains(IOPath.GetFullPath(vm.Vmx));
            dot.Fill = vm.Missing ? Brushes.Red : on ? Brushes.LimeGreen : Brushes.Gray;
            dot.ToolTip = vm.Missing ? "Файл не найден" : on ? "Включена" : "Выключена";
        }
    }

    async Task Poll()
    {
        if (busy || polling) return;
        polling = true;
        try { running = await Vmware.ListRunning(); }
        catch { running = new(StringComparer.OrdinalIgnoreCase); } // vmrun missing or failing: show everything as stopped
        finally { polling = false; }
        UpdateDots();
    }

    async Task Busy(Func<Task> work)
    {
        if (busy) return;
        busy = true;
        MainMenu.IsEnabled = Board.IsEnabled = false;
        Cursor = Cursors.Wait;
        Status.Text = "Выполняется…";
        try { await work(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Visual VMs", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            if (Status.Text == "Выполняется…") Status.Text = "";
            busy = false;
            MainMenu.IsEnabled = Board.IsEnabled = true;
            Cursor = null;
        }
    }

    bool Confirm(string text) => MessageBox.Show(this, text, "Visual VMs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    void Error(string text) => MessageBox.Show(this, text, "Visual VMs", MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>Small modal text input built in code.</summary>
    string? Prompt(string text)
    {
        var tb = new TextBox { Margin = new(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var w = new Window
        {
            Title = "Visual VMs", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new(12), Width = 300,
                Children =
                {
                    new TextBlock { Text = text }, tb,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok, new Button { Content = "Отмена", IsCancel = true, Width = 80, Margin = new(8, 0, 0, 0) } } },
                },
            },
        };
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => tb.Focus();
        return w.ShowDialog() == true ? tb.Text.Trim() : null;
    }

    // ---- interaction ----

    object? HitTag(object source)
    {
        for (var d = source as DependencyObject; d != null && d != Board; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: { } t }) return t;
        return null;
    }

    static bool IsDevice(object? t) => t is VmNode or HubNode || t == InternetKey;

    void SetMode(Mode m, string status = "")
    {
        mode = m;
        Status.Text = status;
        if (m == Mode.Idle && rubber != null) { Board.Children.Remove(rubber); rubber = null; cableFrom = null; }
    }

    void StartCable(object from)
    {
        cableFrom = from;
        var c = Center(from);
        rubber = new Line { X1 = c.X, Y1 = c.Y, X2 = c.X, Y2 = c.Y, Stroke = Brushes.DimGray, StrokeThickness = 2, StrokeDashArray = { 4, 3 }, IsHitTestVisible = false };
        Board.Children.Add(rubber);
        SetMode(Mode.Cable, "Выберите второе устройство (Esc — отмена)");
    }

    async void Board_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(Board);
        var t = HitTag(e.OriginalSource);
        switch (mode)
        {
            case Mode.Idle when e.ClickCount == 2 && t is VmNode vm:
                drag = null;
                Board.ReleaseMouseCapture();
                OpenConsole(vm);
                break;
            case Mode.Idle when IsDevice(t):
                (drag, dragStart, dragMoved, dragOffset) = (t, p, false, p - Center(t!));
                Board.CaptureMouse();
                break;
            case Mode.PickSource when IsDevice(t):
                StartCable(t!);
                break;
            case Mode.Cable when IsDevice(t):
                await Connect(cableFrom!, t!);
                break;
            case Mode.PickConnection when t is Edge edge:
                SetMode(Mode.Idle);
                await RemoveEdge(edge);
                break;
            case Mode.PickConnection when t is HubNode hub:
                SetMode(Mode.Idle);
                await RemoveHub(hub);
                break;
        }
    }

    void Board_MouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Board);
        if (rubber != null) (rubber.X2, rubber.Y2) = (p.X, p.Y);
        if (drag == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!dragMoved && (p - dragStart).Length < 4) return;
        dragMoved = true;
        SetCenter(drag, p - dragOffset);
        Redraw();
    }

    void Board_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // opened on button-up so the release cannot click a menu item
        if (mode == Mode.PickDelete && HitTag(e.OriginalSource) is VmNode vm)
        {
            SetMode(Mode.Idle);
            new ContextMenu { ItemsSource = DeleteItems(vm), PlacementTarget = Board, IsOpen = true };
            return;
        }
        if (drag == null) return;
        Board.ReleaseMouseCapture();
        if (dragMoved) MarkDirty();
        else if (drag is HubNode h) StartCable(h); // a click on a hub starts a cable from it
        drag = null;
    }

    void Board_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (mode != Mode.Idle) { SetMode(Mode.Idle); return; }
        menuPoint = e.GetPosition(Board);
        var t = HitTag(e.OriginalSource);
        var items = t switch
        {
            VmNode vm => new[]
            {
                Item("Включить", () => Busy(async () => { await Vmware.Start(vm.Vmx); running.Add(IOPath.GetFullPath(vm.Vmx)); UpdateDots(); })),
                Item("Выключить", () => Busy(() => Vmware.StopSoftThenHard(vm.Vmx))),
                Item("Открыть консоль", () => { OpenConsole(vm); return Task.CompletedTask; }),
                Sub("Удалить", DeleteItems(vm)),
            },
            HubNode hub => new[] { Item("Удалить сегмент", () => RemoveHub(hub)) },
            Edge edge => new[] { Item("Удалить подключение", () => RemoveEdge(edge)) },
            null => CanvasMenu(),
            _ => Array.Empty<MenuItem>(),
        };
        if (items.Length > 0) new ContextMenu { ItemsSource = items, PlacementTarget = Board, IsOpen = true };
    }

    MenuItem[] CanvasMenu() => new[]
    {
        Item("Создать вм", CreateVm),
        Item("Добавить вм", () => { AddVms(); return Task.CompletedTask; }),
        Item("Удалить вм", () => { SetMode(Mode.PickDelete, "Выберите ВМ (Esc — отмена)"); return Task.CompletedTask; }),
        Sub("Сеть вм", new[]
        {
            Item("Соединить напрямую", () => { SetMode(Mode.PickSource, "Выберите первое устройство (Esc — отмена)"); return Task.CompletedTask; }),
            Item("Соединить несколько", NewHub),
            Item("Удалить подключение", () => { SetMode(Mode.PickConnection, "Выберите подключение (Esc — отмена)"); return Task.CompletedTask; }),
        }),
    };

    static MenuItem Item(string header, Func<Task> click)
    {
        var m = new MenuItem { Header = header };
        m.Click += async (_, _) => await click();
        return m;
    }

    static MenuItem Sub(string header, MenuItem[] items) => new() { Header = header, ItemsSource = items };

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && mode != Mode.Idle) { SetMode(Mode.Idle); e.Handled = true; }
        else if (e.Key == Key.F5 && !busy) { Resync(); e.Handled = true; }
    }

    // ---- actions ----

    void OpenConsole(VmNode vm)
    {
        try { Vmware.OpenConsole(vm.Vmx); }
        catch (Exception ex) { Error(ex.Message); }
    }

    async Task CreateVm()
    {
        var d = new CreateVmWindow { Owner = this };
        if (d.ShowDialog() != true) return;
        var (r, at) = (d.Request!, menuPoint);
        await Busy(async () =>
        {
            var vm = new VmNode { Vmx = await Vmware.CreateVm(r), Purpose = r.Purpose };
            SetCenter(vm, at);
            scheme.Vms.Add(vm);
            MarkDirty();
        });
        Resync();
    }

    void AddVms()
    {
        var have = scheme.Vms.Select(v => IOPath.GetFullPath(v.Vmx)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<(string Name, string Vmx)> list;
        try { list = Vmware.ReadInventory().Where(x => !have.Contains(x.Vmx)).DistinctBy(x => x.Vmx, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) { Error("Не удалось прочитать список ВМ VMware: " + ex.Message); return; }
        if (list.Count == 0) { Error("В VMware нет ВМ, которых ещё нет на схеме"); return; }
        var d = new AddVmsWindow(list) { Owner = this };
        if (d.ShowDialog() != true) return;
        int cols = (int)Math.Ceiling(Math.Sqrt(d.Result.Count));
        for (int i = 0; i < d.Result.Count; i++)
        {
            var vm = new VmNode { Vmx = d.Result[i].Vmx, Purpose = d.Result[i].Purpose };
            SetCenter(vm, new(menuPoint.X + i % cols * 140, menuPoint.Y + i / cols * 140));
            scheme.Vms.Add(vm);
        }
        MarkDirty();
        Resync();
    }

    MenuItem[] DeleteItems(VmNode vm) => new[]
    {
        Item("Убрать со схемы", () =>
        {
            if (Confirm($"Убрать {vm.Name} со схемы? ВМ останется в VMware.")) { scheme.Vms.Remove(vm); MarkDirty(); Resync(); }
            return Task.CompletedTask;
        }),
        Item("Удалить с диска", async () =>
        {
            if (!Confirm($"Удалить {vm.Name} с диска безвозвратно? Включённая ВМ будет выключена.")) return;
            await Busy(async () =>
            {
                try { await Vmware.DeleteVm(vm.Vmx); }
                catch (Exception ex) { throw new InvalidOperationException(ex.Message + "\n\nВозможно, ВМ открыта во вкладке VMware — закройте вкладку и повторите."); }
                scheme.Vms.Remove(vm);
                MarkDirty();
            });
            Resync();
        }),
    };

    /// <summary>LAN segment id by name; warns when the Workstation GUI may overwrite preferences.ini.</summary>
    string EnsureSegment(string name)
    {
        bool gui = Vmware.GuiRunning;
        var id = Vmware.EnsureSegment(name, out bool wrote);
        if (wrote && gui) Status.Text = GuiWarning;
        return id;
    }

    async Task Apply(List<(string Vmx, Action<KvFile> Edit)> edits)
    {
        try { await Vmware.ApplyAsync(edits); }
        finally { Resync(); }
    }

    async Task Connect(object a, object b)
    {
        if (a == b || (a is not VmNode && b is not VmNode)) { Status.Text = "Нельзя соединить — выберите другое устройство (Esc — отмена)"; return; }
        var vm = a as VmNode ?? (VmNode)b;
        var other = a is VmNode ? b : a;
        if (vm.Missing || other is VmNode { Missing: true }) { Status.Text = "Нельзя соединить: файл ВМ не найден (Esc — отмена)"; return; }
        SetMode(Mode.Idle);
        if (other is VmNode vm2)
        {
            if (edges.Any(e => e.Kind == EdgeKind.Direct && e.Ends.Any(x => x.Vm == vm) && e.Ends.Any(x => x.Vm == vm2))) { Error("Уже соединены"); return; }
            var seg = $"{vm.Name}-{vm2.Name}";
            if (!Confirm($"Соединить {vm.Name} и {vm2.Name} (сегмент {seg})?")) return;
            await Busy(async () =>
            {
                var id = EnsureSegment(seg);
                await Apply(new() { (vm.Vmx, k => k.AddNic("pvn", id)), (vm2.Vmx, k => k.AddNic("pvn", id)) });
            });
        }
        else if (other is HubNode hub)
        {
            if (vm.Nics.Any(n => n.Type == "pvn" && string.Equals(n.PvnId, hub.PvnId, StringComparison.OrdinalIgnoreCase))) Error("Уже подключена");
            else if (Confirm($"Подключить {vm.Name} к сегменту {hub.Name}?"))
                await Busy(() => Apply(new() { (vm.Vmx, k => k.AddNic("pvn", hub.PvnId)) }));
        }
        else if (vm.Nics.Any(n => n.Type == "nat")) Error("Уже подключена к Интернету");
        else if (Confirm($"Подключить {vm.Name} к Интернету (NAT)?"))
            await Busy(() => Apply(new() { (vm.Vmx, k => k.AddNic("nat")) }));
    }

    async Task RemoveEdge(Edge edge)
    {
        var label = edge.Kind == EdgeKind.Nat ? "NAT" : edge.Hub?.Name ?? Topology.SegmentName(edge.PvnId!, segments);
        var names = string.Join(", ", edge.Ends.Select(x => x.Vm.Name).Distinct());
        if (!Confirm($"Удалить подключение {label} ({names})?")) return;
        await Busy(() => Apply(edge.Ends.Select(x => (x.Vm.Vmx, (Action<KvFile>)(k =>
        {
            // the vmx may have changed since the last refresh: never remove a different adapter
            var nic = k.Nics().FirstOrDefault(n => n.Index == x.Nic);
            bool same = nic != null && (edge.Kind == EdgeKind.Nat ? nic.Type == "nat" : nic.Type == "pvn" && string.Equals(nic.PvnId, edge.PvnId, StringComparison.OrdinalIgnoreCase));
            if (!same) throw new InvalidOperationException($"Сеть ВМ {x.Vm.Name} изменилась — нажмите F5 и повторите");
            k.RemoveNic(x.Nic);
        }))).ToList()));
    }

    async Task RemoveHub(HubNode hub)
    {
        bool Uses(Nic n) => n.Type == "pvn" && string.Equals(n.PvnId, hub.PvnId, StringComparison.OrdinalIgnoreCase);
        var vms = scheme.Vms.Where(v => v.Nics.Any(Uses)).ToList();
        if (!Confirm($"Удалить сегмент {hub.Name} и его адаптеры у {vms.Count} ВМ?")) return;
        await Busy(async () =>
        {
            if (vms.Count > 0 && !await Vmware.ApplyAsync(vms.Select(v => (v.Vmx, (Action<KvFile>)(k =>
                {
                    foreach (var n in k.Nics().Where(Uses)) k.RemoveNic(n.Index);
                }))).ToList())) return;
            scheme.Hubs.Remove(hub);
            MarkDirty();
        });
        Resync();
    }

    async Task NewHub()
    {
        var name = Prompt("Имя сегмента");
        if (name == null) return;
        if (name.Length == 0 || name.IndexOfAny(new[] { '"', '|' }) >= 0) { Error("Укажите имя сегмента без символов \" и |"); return; }
        if (scheme.Hubs.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { Error("Сегмент с таким именем уже есть на схеме"); return; }
        await Busy(() =>
        {
            var id = EnsureSegment(name);
            if (scheme.Hubs.Any(h => h.PvnId.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Этот сегмент уже есть на схеме");
            scheme.Hubs.Add(new HubNode { Name = name, PvnId = id, X = menuPoint.X, Y = menuPoint.Y });
            MarkDirty();
            Resync();
            return Task.CompletedTask;
        });
    }

    // ---- files ----

    void OpenScheme(string file)
    {
        try { scheme = Scheme.Load(file); }
        catch (Exception ex) { Error($"Не удалось открыть {file}: {ex.Message}"); return; }
        path = file;
        dirty = false;
        RememberPath();
        // self-heal: the Workstation GUI may have dropped our segment names from preferences.ini
        var none = new Dictionary<string, string>();
        if (!Vmware.GuiRunning)
            foreach (var h in scheme.Hubs.Where(h => h.Name != Topology.SegmentName(h.PvnId, none)))
                try { Vmware.EnsureSegment(h.Name, h.PvnId); }
                catch (Exception ex) { Status.Text = "preferences.ini: " + ex.Message; }
        Resync();
        UpdateTitle();
    }

    void RememberPath()
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(LastPath)!);
        File.WriteAllText(LastPath, path);
    }

    bool Save()
    {
        if (path == null) return SaveAs();
        try { scheme.Save(path); }
        catch (Exception ex) { Error(ex.Message); return false; }
        dirty = false;
        UpdateTitle();
        return true;
    }

    bool SaveAs()
    {
        var d = new SaveFileDialog { Filter = "Схема Visual VMs (*.vvm)|*.vvm", DefaultExt = ".vvm" };
        if (d.ShowDialog(this) != true) return false;
        path = d.FileName;
        if (!Save()) return false;
        RememberPath();
        return true;
    }

    /// <summary>Returns false if the user cancelled.</summary>
    bool AskSave()
    {
        if (!dirty) return true;
        return MessageBox.Show(this, "Сохранить изменения?", "Visual VMs", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!AskSave()) return;
        SetMode(Mode.Idle);
        (scheme, path, dirty) = (new(), null, false);
        Resync();
        UpdateTitle();
    }

    void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!AskSave()) return;
        var d = new OpenFileDialog { Filter = "Схема Visual VMs (*.vvm)|*.vvm" };
        if (d.ShowDialog(this) != true) return;
        SetMode(Mode.Idle);
        OpenScheme(d.FileName);
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    void Refresh_Click(object sender, RoutedEventArgs e) => Resync();

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (busy || !AskSave()) e.Cancel = true;
    }
}
