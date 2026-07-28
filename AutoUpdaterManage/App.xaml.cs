using System.Configuration;
using System.Data;
using System.Windows;
using DevExpress.Xpf.Core;

namespace AutoUpdaterManage
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public App()
        {
            ApplicationThemeHelper.ApplicationThemeName = "Win11Light";
        }
    }

}
