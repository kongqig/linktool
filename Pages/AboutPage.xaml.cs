using System.Reflection;
using System.Windows.Controls;

namespace linktool.Pages
{
    /// <summary>关于页：版本信息</summary>
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version != null ? $"版本 {version}" : "版本 1.0";
        }
    }
}