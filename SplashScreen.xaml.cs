using Microsoft.UI.Xaml.Controls;
using CryptoTax2026.Services;

namespace CryptoTax2026
{
    public sealed partial class SplashScreen : Page
    {
        public SplashScreen()
        {
            this.InitializeComponent();
            var version = UpdateCheckService.GetCurrentVersion();
            VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }
}
