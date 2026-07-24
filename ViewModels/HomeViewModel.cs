using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SortAndDelete.Helpers;
using SortAndDelete.Logic;
using SortAndDelete.Models;
using SortAndDelete.Services;
using SortAndDelete.Views;

namespace SortAndDelete.ViewModels;

/// <summary>
/// One row in the months list. Observable so progress can be refreshed *in place* —
/// rebuilding the collection would reset the list's scroll position on every return.
/// </summary>
public partial class MonthCard : ObservableObject
{
    private readonly Func<string, ImageSource> _thumbFactory;
    private string? _coverPhotoId;

    public string MonthKey { get; }
    public string DisplayName { get; }

    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool isDone;
    [ObservableProperty] private string statusGlyph = "›";
    [ObservableProperty] private ImageSource? cover;
    [ObservableProperty] private bool isSelected;

    public MonthCard(MonthGroup month, Func<string, ImageSource> thumbFactory)
    {
        _thumbFactory = thumbFactory;
        MonthKey = month.MonthKey;
        DisplayName = month.DisplayName;
        Update(month);
    }

    public void Update(MonthGroup month)
    {
        ProgressText = month.ProgressText;
        Progress = month.Progress;
        IsDone = month.IsDone;
        StatusGlyph = month.StatusGlyph;
        if (month.CoverPhotoId != _coverPhotoId)
        {
            _coverPhotoId = month.CoverPhotoId;
            Cover = _coverPhotoId is null ? null : _thumbFactory(_coverPhotoId);
        }
    }
}

/// <summary>One chip in the folder filter row. FolderId is null for the "All" chip.</summary>
public partial class FolderChip(string? folderId, string label) : ObservableObject
{
    public string? FolderId => folderId;
    public string Label => label;

    [ObservableProperty] private bool isSelected;
}

public partial class HomeViewModel(GalleryService gallery) : ObservableObject
{
    private const string LastMonthPreferenceKey = "last_month_key";

    public ObservableCollection<MonthCard> Months { get; } = [];
    public ObservableCollection<FolderChip> Folders { get; } = [];

    private string? _selectedFolderId;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool hasAccess = true;
    [ObservableProperty] private string totalPhotosText = "";
    [ObservableProperty] private string reviewedText = "";
    [ObservableProperty] private string freedText = "";
    [ObservableProperty] private bool hasFreedSpace;
    [ObservableProperty] private MonthCard? continueMonth;
    [ObservableProperty] private int binCount;
    [ObservableProperty] private string binBadgeText = "Bin";
    [ObservableProperty] private string streakText = "";
    [ObservableProperty] private bool hasStreak;
    [ObservableProperty] private bool isSelectionMode;
    [ObservableProperty] private bool hasSelectedMonths;
    [ObservableProperty] private string reReviewButtonText = "";

    // Separate from IsLoading: RefreshView sets IsLoading=true via its two-way
    // IsRefreshing binding *before* the refresh command runs, so IsLoading can't
    // double as the re-entrancy guard.
    private bool _loadInProgress;

    public async Task LoadAsync(bool refresh = false)
    {
        if (_loadInProgress)
            return;

        _loadInProgress = true;
        IsLoading = true;
        try
        {
            HasAccess = await gallery.Library.RequestAccessAsync();
            if (!HasAccess)
            {
                Months.Clear();
                return;
            }

            var groups = await gallery.GetMonthGroupsAsync(refresh, _selectedFolderId);
            var reviews = await gallery.Store.GetAllAsync();
            var pending = reviews.Where(r => r.Decision == SwipeDecision.Trash && r.CommittedAt == null).ToList();
            var freedBytes = await gallery.Store.GetFreedBytesAsync();

            BinCount = pending.Count;
            BinBadgeText = BinCount == 0 ? "Bin" : $"Bin · {BinCount}";
            HasFreedSpace = freedBytes > 0;
            FreedText = $"{ByteFormat.Human(freedBytes)} freed";

            int total = groups.Sum(g => g.TotalCount);
            int reviewed = groups.Sum(g => g.ReviewedCount);
            TotalPhotosText = $"{total:N0} items";
            ReviewedText = total == 0 ? "0% reviewed" : $"{(int)Math.Round(100.0 * reviewed / total)}% reviewed";

            // Streak
            var streak = StreakCalculator.Compute(
                reviews.Select(r => r.DecidedAt.ToLocalTime()),
                DateOnly.FromDateTime(DateTime.Now));
            HasStreak = streak.StreakDays > 0;
            StreakText = streak.StreakDays > 0
                ? $"🔥 {streak.StreakDays}-day streak · {streak.ReviewedToday} today"
                : "";

            await LoadFoldersAsync();

            // Same months in the same order → update rows in place so the
            // CollectionView keeps its scroll position when we come back here.
            bool sameShape = Months.Count == groups.Count;
            if (sameShape)
            {
                for (int i = 0; i < groups.Count && sameShape; i++)
                    sameShape = Months[i].MonthKey == groups[i].MonthKey;
            }

            if (sameShape)
            {
                for (int i = 0; i < groups.Count; i++)
                    Months[i].Update(groups[i]);
            }
            else
            {
                Months.Clear();
                foreach (var group in groups)
                    Months.Add(new MonthCard(group, id => gallery.CreateThumbnail(id, 160)));
            }

            var lastKey = Preferences.Default.Get(LastMonthPreferenceKey, "");
            ContinueMonth = Months.FirstOrDefault(m => m.MonthKey == lastKey && !m.IsDone);
        }
        finally
        {
            _loadInProgress = false;
            IsLoading = false;
        }
    }

    private async Task LoadFoldersAsync()
    {
        var albums = await gallery.Library.GetAlbumsAsync();
        var photos = await gallery.GetPhotosAsync();
        int favorites = photos.Count(p => p.IsFavorite);

        Folders.Clear();
        var all = new FolderChip(null, "All folders") { IsSelected = _selectedFolderId is null };
        Folders.Add(all);

        // The one virtual album Android exposes system-wide; Samsung/Google
        // gallery "Favourites" show up here.
        if (favorites > 0)
        {
            Folders.Add(new FolderChip(GalleryService.FavoritesFolderId, $"⭐ Favourites · {favorites}")
            {
                IsSelected = _selectedFolderId == GalleryService.FavoritesFolderId,
            });
        }

        foreach (var album in albums.Where(a => a.Count > 0))
            Folders.Add(new FolderChip(album.Id, $"{album.Name} · {album.Count}") { IsSelected = album.Id == _selectedFolderId });

        // Selected folder disappeared (e.g. emptied) → fall back to All.
        if (_selectedFolderId is not null && !Folders.Any(f => f.IsSelected))
        {
            _selectedFolderId = null;
            all.IsSelected = true;
        }
    }

    [RelayCommand]
    private async Task SelectFolder(FolderChip? chip)
    {
        if (chip is null)
            return;

        _selectedFolderId = chip.FolderId;
        foreach (var folder in Folders)
            folder.IsSelected = ReferenceEquals(folder, chip);

        await LoadAsync();
    }

    [RelayCommand]
    private Task Refresh()
    {
        gallery.Invalidate();
        return LoadAsync(refresh: true);
    }

    [RelayCommand]
    private async Task OpenMonth(MonthCard? month)
    {
        if (month is null)
            return;

        var route = $"{nameof(SwipePage)}?mode=month&month={month.MonthKey}";
        if (_selectedFolderId is not null)
            route += $"&folder={Uri.EscapeDataString(_selectedFolderId)}";
        await Shell.Current.GoToAsync(route);
    }

    // ---------- Re-review month selection ----------

    /// <summary>A month row tap either opens the deck or toggles the selection.</summary>
    [RelayCommand]
    private Task MonthTapped(MonthCard? month)
    {
        if (month is null)
            return Task.CompletedTask;

        if (!IsSelectionMode)
            return OpenMonth(month);

        month.IsSelected = !month.IsSelected;
        UpdateSelectionState();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void StartReReviewSelection()
    {
        foreach (var month in Months)
            month.IsSelected = false;
        IsSelectionMode = true;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void SelectAllMonths()
    {
        foreach (var month in Months)
            month.IsSelected = true;
        UpdateSelectionState();
    }

    [RelayCommand]
    private void CancelSelection()
    {
        IsSelectionMode = false;
        foreach (var month in Months)
            month.IsSelected = false;
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        int count = Months.Count(m => m.IsSelected);
        HasSelectedMonths = IsSelectionMode && count > 0;
        ReReviewButtonText = $"↺ Re-review {count} month{(count == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private async Task ReReviewSelected()
    {
        var selected = Months.Where(m => m.IsSelected).ToList();
        if (selected.Count == 0)
            return;

        bool confirmed = await Shell.Current.DisplayAlertAsync("Re-review months",
            $"Review {selected.Count} month{(selected.Count == 1 ? "" : "s")} again from the start? " +
            "Your keep decisions there are forgotten. Photos in the bin and the system trash are not touched.",
            "Re-review", "Cancel");
        if (!confirmed)
            return;

        foreach (var month in selected)
            await gallery.Store.ClearKeepDecisionsForMonthAsync(month.MonthKey);

        CancelSelection();
        await LoadAsync();
    }

    [RelayCommand]
    private Task Continue() => OpenMonth(ContinueMonth);

    [RelayCommand]
    private Task OpenBin() => Shell.Current.GoToAsync(nameof(BinPage));

    [RelayCommand]
    private Task OpenBiggest() => Shell.Current.GoToAsync($"{nameof(SwipePage)}?mode=biggest");

    [RelayCommand]
    private async Task GrantAccess()
    {
        // A second in-app request only works if the user hasn't permanently denied;
        // otherwise send them to the system settings page for the app.
        HasAccess = await gallery.Library.RequestAccessAsync();
        if (HasAccess)
            await LoadAsync();
        else
            AppInfo.Current.ShowSettingsUI();
    }
}
