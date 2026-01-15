using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using LenovoLegionToolkit.Lib.Extensions;
using LenovoLegionToolkit.Lib.Utils;
using LenovoLegionToolkit.WPF.Resources;

namespace LenovoLegionToolkit.WPF.Pages;

public partial class AboutPage
{
    private static string VersionText
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
                return string.Empty;
            if (version.IsBeta())
                return "BETA";
            return version.ToString(3);
        }
    }

    private static string BuildText
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly is null)
                return string.Empty;
            
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute is null)
                return string.Empty;
            
            var informationalVersion = attribute.InformationalVersion;
            
            // 格式：2.26.1.21+gd7d46df9+2026-01-16 00:14:56
            if (informationalVersion.Contains('+'))
            {
                var parts = informationalVersion.Split('+');
                if (parts.Length > 2)
                {
                    return parts[2]; // 构建时间（例如：2026-01-16 00:14:56）
                }
            }
            
            return string.Empty;
        }
    }

    private static string CommitText
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();
            if (assembly is null)
                return string.Empty;
            
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute is null)
                return string.Empty;
            
            var informationalVersion = attribute.InformationalVersion;
            
            // 格式：2.26.1.21+gd7d46df9+2026-01-16 00:14:56
            if (informationalVersion.Contains('+'))
            {
                var parts = informationalVersion.Split('+');
                if (parts.Length > 1)
                {
                    return parts[1]; // 提交哈希（例如：gd7d46df9）
                }
            }
            
            return string.Empty;
        }
    }

    private static string CopyrightText
    {
        get
        {
            var location = Assembly.GetEntryAssembly()?.Location;
            if (location is null)
                return string.Empty;
            return FileVersionInfo.GetVersionInfo(location).LegalCopyright ?? string.Empty;
        }
    }

    public AboutPage()
    {
        InitializeComponent();

        _version.Text += $" {VersionText}";
        _build.Text += $" {BuildText}";
        _commit.Text += $" {CommitText}";
        _copyright.Text = CopyrightText;

        _translationCredit.Visibility = Resource.Culture.Equals(new CultureInfo("en")) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpenApplicationDataFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(Folders.AppData))
            return;

        Process.Start("explorer", Folders.AppData);
    }

    private void OpenApplicationTempFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(Folders.Temp))
            return;

        Process.Start("explorer", Folders.Temp);
    }
}
