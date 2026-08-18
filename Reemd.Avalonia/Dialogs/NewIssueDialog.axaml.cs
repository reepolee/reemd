using System.ComponentModel;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Reemd.Services;

namespace Reemd.Dialogs;

/// <summary>
/// Last-shown position and size of NewIssueDialog, persisted so it reopens where the
/// user left it instead of always centering on the (possibly hidden) main window.
/// </summary>
internal sealed class DialogPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// One selectable GitHub label checkbox in the New Issue dialog.
/// </summary>
public sealed class LabelItem
{
    public required string Name { get; init; }
    public required IBrush ColorBrush { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>
/// Dialog for creating a new GitHub issue on a reepolee "ree*" repo.
/// </summary>
public partial class NewIssueDialog : Window
{
    private readonly GitHubService _gitHubService;

    private static readonly string PlacementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reemd", "new_issue_dialog_placement.json");

    private static readonly (string Name, string Hex)[] DefaultLabels =
    {
        ("bug", "#D73A4A"),
        ("documentation", "#0075CA"),
        ("duplicate", "#CFD3D7"),
        ("enhancement", "#A2EEEF"),
        ("good first issue", "#7057FF"),
        ("help wanted", "#008672"),
        ("invalid", "#E4E669"),
        ("question", "#D876E3"),
        ("wontfix", "#FFFFFF"),
    };

    // Parameterless ctor required by the Avalonia XAML compiler (never used at runtime).
    public NewIssueDialog() : this(new GitHubService(), false)
    {
    }

    public NewIssueDialog(GitHubService gitHubService, bool isDarkMode)
    {
        InitializeComponent();
        _gitHubService = gitHubService;

        LabelsList.ItemsSource = DefaultLabels
            .Select(l => new LabelItem { Name = l.Name, ColorBrush = new SolidColorBrush(Color.Parse(l.Hex)) })
            .ToList();

        if (isDarkMode)
            ApplyDarkTheme();

        RestorePlacement();

        Opened += NewIssueDialog_Opened;
        Closing += NewIssueDialog_Closing;
    }

    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementPath)) return;
            var json = File.ReadAllText(PlacementPath);
            var placement = JsonSerializer.Deserialize<DialogPlacement>(json);
            if (placement == null || placement.Width <= 0 || placement.Height <= 0) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)placement.Left, (int)placement.Top);
            Width = placement.Width;
            Height = placement.Height;
        }
        catch
        {
            // Best-effort — fall back to CenterOwner if the file is missing/corrupt.
        }
    }

    private void NewIssueDialog_Closing(object? sender, WindowClosingEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(PlacementPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var placement = new DialogPlacement
            {
                Left = Position.X,
                Top = Position.Y,
                Width = Width,
                Height = Height
            };
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Best-effort
        }
    }

    private void ApplyDarkTheme()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Foreground = new SolidColorBrush(Colors.White);
        RepoCombo.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        RepoCombo.Foreground = new SolidColorBrush(Colors.White);
        TitleTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        TitleTextBox.Foreground = new SolidColorBrush(Colors.White);
        DescriptionTextBox.Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C));
        DescriptionTextBox.Foreground = new SolidColorBrush(Colors.White);
    }

    private void NewIssueDialog_Opened(object? sender, EventArgs e)
    {
        TitleTextBox.Focus();

        var usedRepos = _gitHubService.UsedRepos;
        RepoCombo.ItemsSource = usedRepos;
        if (usedRepos.Count > 0)
            RepoCombo.SelectedIndex = 0;

        StatusTextBlock.Text = usedRepos.Count == 0
            ? "No repos used yet. Press Reload to fetch all repos from GitHub."
            : string.Empty;
    }

    private async void BtnReload_Click(object? sender, RoutedEventArgs e)
    {
        StatusTextBlock.Text = "Loading all repositories from GitHub...";
        BtnReload.IsEnabled = false;

        try
        {
            var selectedRepo = RepoCombo.SelectedItem as string;
            var repos = await _gitHubService.ListReeRepositoriesAsync();

            RepoCombo.ItemsSource = repos;
            if (selectedRepo != null && repos.Contains(selectedRepo))
                RepoCombo.SelectedItem = selectedRepo;
            else if (repos.Count > 0)
                RepoCombo.SelectedIndex = 0;

            StatusTextBlock.Text = repos.Count == 0 ? "No matching repositories found." : string.Empty;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Failed to load repositories: {ex.Message}";
        }
        finally
        {
            BtnReload.IsEnabled = true;
        }
    }

    private async void BtnSubmit_Click(object? sender, RoutedEventArgs e)
    {
        var repo = RepoCombo.SelectedItem as string;
        var title = TitleTextBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(repo))
        {
            StatusTextBlock.Text = "Select a repository.";
            return;
        }
        if (string.IsNullOrEmpty(title))
        {
            StatusTextBlock.Text = "Enter a title.";
            return;
        }

        BtnSubmit.IsEnabled = false;
        StatusTextBlock.Text = "Creating issue...";

        var labels = LabelsList.ItemsSource as IEnumerable<LabelItem>;
        var selectedLabels = (labels ?? [])
            .Where(l => l.IsChecked)
            .Select(l => l.Name)
            .ToList();

        var (success, message) = await _gitHubService.CreateIssueAsync(repo, title, DescriptionTextBox.Text ?? "", selectedLabels);

        if (success)
        {
            _gitHubService.RecordRepoUsed(repo);
            Close(true);
        }
        else
        {
            StatusTextBlock.Text = message;
            BtnSubmit.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    #region Description Context Menu

    private void DescriptionContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        DescMenuUndo.IsEnabled = DescriptionTextBox.CanUndo;
        DescMenuRedo.IsEnabled = DescriptionTextBox.CanRedo;
    }

    private void DescMenu_Undo_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.Undo();
    private void DescMenu_Redo_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.Redo();
    private void DescMenu_Cut_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.Cut();
    private void DescMenu_Copy_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.Copy();
    private void DescMenu_Paste_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.Paste();
    private void DescMenu_SelectAll_Click(object? sender, RoutedEventArgs e) => DescriptionTextBox.SelectAll();

    #endregion
}
