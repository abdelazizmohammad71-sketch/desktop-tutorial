using Microsoft.UI.Xaml.Controls;
using ZX0ai.ViewModels;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace ZX0ai.Views.Controls
{
    public sealed partial class ActivityTimelineView : UserControl
    {
        public ActivityTimelineViewModel ViewModel { get; } = new ActivityTimelineViewModel();

        public ActivityTimelineView()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }

        public async Task StartDemoAsync() => await ViewModel.StartDemoAsync();
    }
}
