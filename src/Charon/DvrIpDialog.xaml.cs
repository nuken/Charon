using System.Windows;

namespace Charon;

public partial class DvrIpDialog : Window
{
    public string DvrIp { get; private set; } = string.Empty;
    public int DvrPort { get; private set; } = 8089;

    public DvrIpDialog(string existingIp, int existingPort)
    {
        InitializeComponent();
        TxtIp.Text = existingIp ?? string.Empty;
        TxtPort.Text = (existingPort > 0 ? existingPort : 8089).ToString();

        Loaded += (_, _) =>
        {
            TxtIp.Focus();
            TxtIp.SelectAll();
        };
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        string rawIp = TxtIp.Text.Trim();
        string rawPort = TxtPort.Text.Trim();

        // Allow user to paste "192.168.86.250:8089" directly into the IP box if they want
        if (rawIp.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            rawIp = rawIp["http://".Length..];
        else if (rawIp.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            rawIp = rawIp["https://".Length..];

        rawIp = rawIp.TrimEnd('/');

        if (rawIp.Contains(':'))
        {
            var parts = rawIp.Split(':', 2);
            rawIp = parts[0].Trim();
            if (!string.IsNullOrWhiteSpace(parts[1]))
                rawPort = parts[1].Trim();
        }

        if (string.IsNullOrWhiteSpace(rawIp))
        {
            MessageBox.Show("Please enter the IP address of your Channels DVR server (for example: 192.168.86.250).",
                "Channels DVR IP Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtIp.Focus();
            return;
        }

        if (!int.TryParse(rawPort, out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Please enter a valid TCP port (default is 8089).",
                "Invalid Port", MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtPort.Focus();
            return;
        }

        DvrIp = rawIp;
        DvrPort = port;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}