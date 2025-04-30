using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using QSCM.Models;
using QSCM.Services;
using System.Collections.ObjectModel;
using System.Windows;

namespace QuadStickConfigManager
{
    public partial class MainWindow : Window
    {
        readonly TemplateService _templates = new();
        // Index of all available templates:
        public ObservableCollection<TemplateInfo> TemplateList { get; }
            = new ObservableCollection<TemplateInfo>();

        Profile? _currentProfile;
        public Profile? CurrentProfile
        {
            get => _currentProfile;
            set { _currentProfile = value; DataContext = null; DataContext = this; }
        }

        TemplateInfo? _selectedTemplate;
        public TemplateInfo? SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                _selectedTemplate = value;
                if (value != null) _ = LoadProfile(value);
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            _ = LoadIndex();  // fire-and-forget
        }

        async Task LoadIndex()
        {
            var all = await _templates.LoadIndexAsync();
            foreach (var t in all) TemplateList.Add(t);
        }

        async Task LoadProfile(TemplateInfo info)
        {
            CurrentProfile = await _templates.LoadProfileAsync(info);
        }

        async void OnFlashClick(object s, RoutedEventArgs e)
        {
            if (CurrentProfile == null)
            {
                MessageBox.Show("Pick a template first!", "Error");
                return;
            }

            var csv = CsvConverter.ToCsv(CurrentProfile.Rows);
            var ok  = await QmpBridge.ImportCsvAsync(csv);
            MessageBox.Show(ok ? "Flashed!" : "QMP not running", "QSCM");
        }
    }
}
