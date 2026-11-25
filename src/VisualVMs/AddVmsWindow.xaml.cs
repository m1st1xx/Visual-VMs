using System.Windows;

namespace VisualVMs;

public partial class AddVmsWindow : Window
{
    public class Row
    {
        public bool Selected { get; set; }
        public string Name { get; set; } = "";
        public string Vmx { get; set; } = "";
        public int PurposeIndex { get; set; } = (int)Purpose.Client;
    }

    readonly List<Row> rows;

    public List<(string Vmx, Purpose Purpose)> Result { get; } = new();

    public AddVmsWindow(List<(string Name, string Vmx)> vms)
    {
        InitializeComponent();
        rows = vms.Select(v => new Row { Name = v.Name, Vmx = v.Vmx }).ToList();
        VmGrid.ItemsSource = rows;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        Result.AddRange(rows.Where(r => r.Selected).Select(r => (r.Vmx, (Purpose)r.PurposeIndex)));
        if (Result.Count == 0) { MessageBox.Show(this, "Отметьте хотя бы одну ВМ", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        DialogResult = true;
    }
}
