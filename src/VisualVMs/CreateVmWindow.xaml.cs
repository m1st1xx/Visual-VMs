using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VisualVMs;

public partial class CreateVmWindow : Window
{
    static readonly (string Label, string GuestOs, int Ram, int Cpu, int Disk)[] Oses =
    {
        ("Debian", "debian13-64", 2048, 2, 20),
        ("Ubuntu", "ubuntu-64", 2048, 2, 20),
        ("Другой Linux 64-бит", "other6xlinux-64", 2048, 2, 20),
        ("Windows 10", "windows9-64", 4096, 2, 60),
        ("Windows 11", "windows11-64", 4096, 2, 64),
        ("Windows Server", "windows2019srv-64", 4096, 2, 60),
        ("Другая", "other-64", 1024, 1, 16),
    };

    public CreateVmRequest? Request { get; private set; }

    public CreateVmWindow()
    {
        InitializeComponent();
        OsBox.ItemsSource = Oses.Select(o => o.Label);
        OsBox.SelectedIndex = 0;
        Loaded += (_, _) => NameBox.Focus();
    }

    void OsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var o = Oses[OsBox.SelectedIndex];
        (RamBox.Text, CpuBox.Text, DiskBox.Text) = (o.Ram.ToString(), o.Cpu.ToString(), o.Disk.ToString());
        Win11Hint.Visibility = o.GuestOs == "windows11-64" ? Visibility.Visible : Visibility.Collapsed;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = "Образ диска (*.iso)|*.iso|Все файлы|*.*" };
        if (d.ShowDialog(this) == true) IsoBox.Text = d.FileName;
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RamBox.Text, out var ram) || !int.TryParse(CpuBox.Text, out var cpu) || !int.TryParse(DiskBox.Text, out var disk))
        {
            MessageBox.Show(this, "ОЗУ, ядра и диск — целые числа", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var r = new CreateVmRequest(NameBox.Text, Oses[OsBox.SelectedIndex].GuestOs, IsoBox.Text.Trim().Trim('"'), ram, cpu, disk, (Purpose)PurposeBox.SelectedIndex);
        if (Vmware.Validate(r, Vmware.DefaultVmPath()) is { } err)
        {
            MessageBox.Show(this, err, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Request = r;
        DialogResult = true;
    }
}
