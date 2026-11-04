using System.Windows.Controls;

namespace Theater3D.App.Panels;

public partial class PropertiesPanel : UserControl
{
    public PropertiesPanel(EditorContext context)
    {
        InitializeComponent();
        DataContext = new PropertiesViewModel(context);
    }
}
