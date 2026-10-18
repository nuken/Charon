using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Charon.Config;
using Charon.Providers;
using Charon.Server;
using Charon.Tuner;
using Microsoft.Win32;

namespace Charon;

public partial class MainWindow : Window
{
    private readonly CharonConfig _config;
    private readonly ProviderManager _providers;
    private readonly ChromeTuner _tuner;
    private readonly CharonServer _server;
    private readonly bool _isInitializing = true;
    private readonly ObservableCollection<ProviderChannel> _editableChannels = [];

    public MainWindow()
    {
        InitializeComponent();

        _config = CharonConfig.LoadOrCreate();
        _providers = new ProviderManager();
        _tuner = new ChromeTuner(_config);
        _server = new CharonServer(_config, _providers, _tuner, AppendLog);
        _server.TunersChanged += UpdateTunerStatus;

        GridChannels.ItemsSource = _editableChannels;

        PopulateUiFromConfig();
        RefreshProviderDropdown();

        _isInitializing = false;

        Loaded += async (_, _) =>
        {
            AppendLog($"[Init] App Directory: {AppContext.BaseDirectory}");
            AppendLog($"[Init] Providers Path: {_providers.ProvidersRoot}");
            AppendLog($"[Init] Chrome Path: {_config.ResolvedChromePath}");
            await _server.StartAsync();
        };

        Closed += async (_, _) =>
        {
            await _server.DisposeAsync();
        };
    }

    private void PopulateUiFromConfig()
    {
        TxtM3uUrl.Text = $"http://{_config.ResolvedLanIp}:{_config.ResolvedPort}/playlist.m3u";
        TxtCustomX.Text = _config.CustomX.ToString();
        TxtCustomY.Text = _config.CustomY.ToString();

        SelectComboItem(CmbEncoder, _config.VideoEncoder);
        SelectComboItem(CmbWindowMode, _config.WindowPlacement.ToString());
    }

    private static void SelectComboItem(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private LoadedProvider? GetSelectedProvider()
    {
        if (_providers.Providers.Count == 0)
            return null;

        int idx = CmbProviders.SelectedIndex;
        if (idx < 0 || idx >= _providers.Providers.Count)
            idx = 0;

        return _providers.Providers[idx];
    }

    private void RefreshProviderDropdown()
    {
        int previousIndex = Math.Max(0, CmbProviders.SelectedIndex);
        _providers.ReloadProviders();

        CmbProviders.ItemsSource = _providers.Providers
            .Select(p => $"{p.Manifest.Name} ({p.Channels.Count} ch)")
            .ToList();

        if (CmbProviders.Items.Count > 0)
        {
            CmbProviders.SelectedIndex = Math.Min(previousIndex, CmbProviders.Items.Count - 1);
            LoadSelectedProviderChannelsIntoEditor();
        }

        AppendLog($"[Providers] Loaded {_providers.Providers.Count} provider(s) from {_providers.ProvidersRoot}");
    }

    private void CmbProviders_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        LoadSelectedProviderChannelsIntoEditor();
    }

    private void LoadSelectedProviderChannelsIntoEditor()
    {
        var provider = GetSelectedProvider();
        _editableChannels.Clear();

        if (provider is null)
        {
            TxtRawChannelsJson.Text = "[]";
            return;
        }

        foreach (var ch in provider.Channels)
        {
            _editableChannels.Add(new ProviderChannel
            {
                Id = ch.Id,
                Name = ch.Name,
                ChannelNumber = ch.ChannelNumber,
                StationId = ch.StationId,
                Url = ch.Url
            });
        }

        TxtRawChannelsJson.Text = ProviderManager.FormatChannelsJson(_editableChannels);
    }

    private void ChkRawJsonMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        bool rawMode = ChkRawJsonMode.IsChecked == true;
        if (rawMode)
        {
            GridChannels.CommitEdit(DataGridEditingUnit.Cell, true);
            GridChannels.CommitEdit(DataGridEditingUnit.Row, true);
            TxtRawChannelsJson.Text = ProviderManager.FormatChannelsJson(_editableChannels);
            GridChannels.Visibility = Visibility.Collapsed;
            TxtRawChannelsJson.Visibility = Visibility.Visible;
        }
        else
        {
            try
            {
                var parsed = ProviderManager.ParseChannelsJson(TxtRawChannelsJson.Text);
                _editableChannels.Clear();
                foreach (var ch in parsed)
                    _editableChannels.Add(ch);

                TxtRawChannelsJson.Visibility = Visibility.Collapsed;
                GridChannels.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid JSON syntax:\n\n{ex.Message}", "JSON Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                ChkRawJsonMode.IsChecked = true;
            }
        }
    }

    private void BtnAddChannelRow_Click(object sender, RoutedEventArgs e)
    {
        if (ChkRawJsonMode.IsChecked == true)
            ChkRawJsonMode.IsChecked = false;

        var newChannel = new ProviderChannel
        {
            Id = $"ch-{_editableChannels.Count + 1}",
            Name = "New Channel",
            ChannelNumber = "",
            StationId = "",
            Url = "https://tv.youtube.com/watch/"
        };

        _editableChannels.Add(newChannel);
        GridChannels.SelectedItem = newChannel;
        GridChannels.ScrollIntoView(newChannel);
    }

    private void BtnDeleteChannelRow_Click(object sender, RoutedEventArgs e)
    {
        if (ChkRawJsonMode.IsChecked == true)
        {
            MessageBox.Show("Uncheck 'Raw JSON View' to delete selected rows, or edit the JSON text directly.", "Channel Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = GridChannels.SelectedItems.OfType<ProviderChannel>().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Select one or more channel rows in the table first.", "Delete Channel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var item in selected)
            _editableChannels.Remove(item);

        TxtRawChannelsJson.Text = ProviderManager.FormatChannelsJson(_editableChannels);
    }

    private void BtnImportChannels_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        if (provider is null)
        {
            MessageBox.Show("No provider is loaded.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select channels.json File to Upload / Import",
            Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*",
            InitialDirectory = Directory.Exists(provider.FolderPath) ? provider.FolderPath : AppContext.BaseDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string uploadedJson = File.ReadAllText(dialog.FileName, Encoding.UTF8);
            var importedChannels = ProviderManager.ParseChannelsJson(uploadedJson);

            var choice = MessageBox.Show(
                $"Loaded {importedChannels.Count} channel(s) from:\n{dialog.FileName}\n\n" +
                "Click YES to REPLACE your current channel list and save immediately.\n" +
                "Click NO to MERGE with your current channel list and save immediately.\n" +
                "Click CANCEL to abort.",
                "Upload / Import channels.json",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
                return;

            if (choice == MessageBoxResult.Yes)
            {
                _editableChannels.Clear();
                foreach (var ch in importedChannels)
                    _editableChannels.Add(ch);
            }
            else if (choice == MessageBoxResult.No)
            {
                foreach (var incoming in importedChannels)
                {
                    var existing = _editableChannels.FirstOrDefault(c =>
                        c.Id.Equals(incoming.Id, StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        existing.Name = incoming.Name;
                        existing.ChannelNumber = incoming.ChannelNumber;
                        existing.StationId = incoming.StationId;
                        existing.Url = incoming.Url;
                    }
                    else
                    {
                        _editableChannels.Add(incoming);
                    }
                }
            }

            // Automatically save to disk so uploading a file applies immediately!
            string savedPath = _providers.SaveProviderChannels(provider, _editableChannels.ToList());
            TxtRawChannelsJson.Text = ProviderManager.FormatChannelsJson(_editableChannels);
            RefreshProviderDropdown();

            AppendLog($"[Editor] Uploaded & saved {_editableChannels.Count} channel(s) to {savedPath}");
            MessageBox.Show($"Successfully uploaded and saved {_editableChannels.Count} channel(s) to:\n{savedPath}",
                "Upload Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not import JSON file:\n\n{ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnExportChannels_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        if (provider is null) return;

        var dialog = new SaveFileDialog
        {
            Title = "Export channels.json Backup",
            FileName = "channels.json",
            Filter = "JSON Files (*.json)|*.json"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            string jsonToExport = ChkRawJsonMode.IsChecked == true
                ? TxtRawChannelsJson.Text
                : ProviderManager.FormatChannelsJson(_editableChannels);

            File.WriteAllText(dialog.FileName, jsonToExport, new UTF8Encoding(false));
            AppendLog($"[Editor] Exported channels backup to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not export JSON file:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnSaveChannels_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        if (provider is null)
        {
            MessageBox.Show("No provider is available to save to.", "Save Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            List<ProviderChannel> channelsToSave;
            if (ChkRawJsonMode.IsChecked == true)
            {
                channelsToSave = ProviderManager.ParseChannelsJson(TxtRawChannelsJson.Text);
                _editableChannels.Clear();
                foreach (var ch in channelsToSave)
                    _editableChannels.Add(ch);
            }
            else
            {
                GridChannels.CommitEdit(DataGridEditingUnit.Cell, true);
                GridChannels.CommitEdit(DataGridEditingUnit.Row, true);

                channelsToSave = _editableChannels
                    .Where(c => !string.IsNullOrWhiteSpace(c.Id) || !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Url))
                    .Select(c => new ProviderChannel
                    {
                        Id = string.IsNullOrWhiteSpace(c.Id)
                            ? c.Name.Trim().ToLowerInvariant().Replace(" ", "-")
                            : c.Id.Trim(),
                        Name = string.IsNullOrWhiteSpace(c.Name) ? c.Id.Trim() : c.Name.Trim(),
                        ChannelNumber = c.ChannelNumber?.Trim() ?? string.Empty,
                        StationId = c.StationId?.Trim() ?? string.Empty,
                        Url = c.Url?.Trim() ?? string.Empty
                    })
                    .ToList();
            }

            string savedPath = _providers.SaveProviderChannels(provider, channelsToSave);
            TxtRawChannelsJson.Text = ProviderManager.FormatChannelsJson(channelsToSave);
            RefreshProviderDropdown();

            AppendLog($"[Editor] Saved {channelsToSave.Count} channel(s) to {savedPath}");
            MessageBox.Show($"Saved {channelsToSave.Count} channel(s) to:\n{savedPath}\n\nYour Channels DVR M3U playlist is updated immediately!",
                "Channels Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save channels.json:\n\n{ex.Message}", "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnCopyM3u_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetDataObject(TxtM3uUrl.Text, false);
            AppendLog($"[UI] Copied M3U URL to clipboard: {TxtM3uUrl.Text}");
        }
        catch (Exception ex)
        {
            AppendLog($"[UI] Clipboard busy, please click Copy again ({ex.Message})");
        }
    }

    private void BtnLoginProvider_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProvider();
        if (provider is null) return;

        AppendLog($"[Auth] Opening visible Chrome login window for {provider.Manifest.Name} ({provider.Manifest.LoginUrl})...");
        AppendLog("[Auth] Sign in to your account, verify video plays, and then close that Chrome window to save your session.");
        _tuner.LaunchInteractiveLogin(provider.Manifest.LoginUrl);
    }

    private void BtnReloadProviders_Click(object sender, RoutedEventArgs e)
    {
        RefreshProviderDropdown();
    }

    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (CmbEncoder.SelectedItem is ComboBoxItem encItem && encItem.Content is string enc)
            _config.VideoEncoder = enc;

        if (CmbWindowMode.SelectedItem is ComboBoxItem modeItem &&
            Enum.TryParse<WindowPlacementMode>(modeItem.Content?.ToString(), out var mode))
        {
            _config.WindowPlacement = mode;
        }

        _config.Save();
        AppendLog($"[Config] Saved: Encoder={_config.VideoEncoder}, WindowPlacement={_config.WindowPlacement}");
    }

    private void CustomCoords_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;

        if (int.TryParse(TxtCustomX.Text, out int x)) _config.CustomX = x;
        if (int.TryParse(TxtCustomY.Text, out int y)) _config.CustomY = y;
        _config.Save();
    }

    private void UpdateTunerStatus()
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtTunerStatus.Text = $"Active Tuners: {_server.ActiveTuners.Count} / {_config.MaxConcurrentTuners}";
        });
    }

    private void AppendLog(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TxtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            TxtLogs.ScrollToEnd();
        });
    }
}