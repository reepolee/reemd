using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
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
/// One selectable GitHub label checkbox in the New Issue dialog, colored to match
/// the repo's default label color from GitHub.
/// </summary>
public sealed class LabelItem
{
    public required string Name { get; init; }
    public required Brush ColorBrush { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>
/// Dialog for creating a new GitHub issue on a reepolee "ree*" repo. Opens instantly with
/// only previously-used repos (from used_repos.json, no GitHub call); Reload fetches the
/// full "ree*" repo list from GitHub for one-off selection without persisting it.
/// </summary>
public partial class NewIssueDialog : Window
{
    private readonly GitHubService _gitHubService;

    private static readonly string PlacementPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reemd", "new_issue_dialog_placement.json");

    // GitHub's default repo labels and their standard colors.
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

    public NewIssueDialog(GitHubService gitHubService, bool isDarkMode)
    {
        InitializeComponent();
        _gitHubService = gitHubService;

        LabelsList.ItemsSource = DefaultLabels
            .Select(l => new LabelItem { Name = l.Name, ColorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(l.Hex)) })
            .ToList();

        if (isDarkMode)
            ApplyDarkTheme();

        RestorePlacement();

        Loaded += NewIssueDialog_Loaded;
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
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;
        }
        catch
        {
            // Best-effort — fall back to CenterOwner if the file is missing/corrupt.
        }
    }

    private void NewIssueDialog_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(PlacementPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var placement = new DialogPlacement { Left = Left, Top = Top, Width = Width, Height = Height };
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(placement));
        }
        catch
        {
            // Best-effort — persistence failure should not block closing.
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

    private void NewIssueDialog_Loaded(object sender, RoutedEventArgs e)
    {
        TitleTextBox.Focus();

        // Instant, no GitHub call: only repos an issue has already been sent to.
        var usedRepos = _gitHubService.UsedRepos;
        RepoCombo.ItemsSource = usedRepos;
        if (usedRepos.Count > 0)
            RepoCombo.SelectedIndex = 0;

        StatusTextBlock.Text = usedRepos.Count == 0
            ? "No repos used yet. Press Reload to fetch all repos from GitHub."
            : string.Empty;
    }

    /// <summary>
    /// Fetches ALL "ree*" repos from GitHub and replaces the combo's contents so the user
    /// can pick a repo never sent an issue to before. Not cached or persisted — the next
    /// time the dialog opens it goes back to just the used-repos list.
    /// </summary>
    private async void BtnReload_Click(object sender, RoutedEventArgs e)
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
        catch (System.Exception ex)
        {
            StatusTextBlock.Text = $"Failed to load repositories: {ex.Message}";
        }
        finally
        {
            BtnReload.IsEnabled = true;
        }
    }

    private async void BtnSubmit_Click(object sender, RoutedEventArgs e)
    {
        var repo = RepoCombo.SelectedItem as string;
        var title = TitleTextBox.Text.Trim();

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

        var selectedLabels = ((List<LabelItem>)LabelsList.ItemsSource)
            .Where(l => l.IsChecked)
            .Select(l => l.Name)
            .ToList();

        var (success, message) = await _gitHubService.CreateIssueAsync(repo, title, DescriptionTextBox.Text, selectedLabels);

        if (success)
        {
            _gitHubService.RecordRepoUsed(repo);
            DialogResult = true;
            Close();
        }
        else
        {
            StatusTextBlock.Text = message;
            BtnSubmit.IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
