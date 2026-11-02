using Avalonia.Controls;

namespace WebAutomater.Gui.Views;

public partial class WebAutomationView : UserControl
{
    private const double FlowZoomStep = 1.2;

    public WebAutomationView()
    {
        InitializeComponent();
        FlowZoomIn.Click += (_, _) => Flow.ZoomBy(FlowZoomStep);
        FlowZoomOut.Click += (_, _) => Flow.ZoomBy(1 / FlowZoomStep);
    }
}
