using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SortAndDelete.Helpers;
using SortAndDelete.Logic;
using SortAndDelete.Models;
using SortAndDelete.Services;

namespace SortAndDelete.ViewModels;

/// <summary>One copy inside a group. Selected means "send this one to the bin".</summary>
public partial class DuplicateItem : ObservableObject
{
    public required string PhotoId { get; init; }
    public required string MonthKey { get; init; }
    public required ImageSource Thumbnail { get; init; }
    public required string DimensionsText { get; init; }
    public required string SizeText { get; init; }
    public required string LocationText { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>The copy the app suggests keeping. Still togglable — this is a suggestion.</summary>
    public bool IsKeeper { get; init; }

    [ObservableProperty] private bool isSelected;
}

/// <summary>One set of copies.</summary>
public partial class DuplicateGroupCard : ObservableObject
{
    public required ObservableCollection<DuplicateItem> Items { get; init; }
    public required string BadgeText { get; init; }
    public required bool IsExact { get; init; }

    [ObservableProperty] private string headerText = "";
}

/// <summary>One entry in the strictness picker.</summary>
public partial class LevelChip : ObservableObject
{
    public required DuplicateLevel Level { get; init; }
    public required string Label { get; init; }

    [ObservableProperty] private string countText = "…";
    [ObservableProperty] private bool isSelected;
}

/// <summary>
/// The duplicates screen.
///
/// Scanning happens once; the five levels are queries over what it produced, so switching
/// between them is instant and never re-reads the library. That is also what makes showing a
/// live count next to every level affordable — the user picks by what each one would actually
/// find rather than by what its name suggests.
/// </summary>
public partial class DuplicatesViewModel(GalleryService gallery, DuplicateScanner scanner) : ObservableObject
{
    private static readonly DuplicateLevel[] AllLevels =
        [DuplicateLevel.Exact, DuplicateLevel.SamePhoto, DuplicateLevel.Adjusted,
         DuplicateLevel.SameMoment, DuplicateLevel.Similar];

    private readonly Dictionary<DuplicateLevel, List<DuplicateGroup>> _byLevel = [];
    private IReadOnlyList<ScannedPhoto> _scanned = [];
    private DuplicateLevel _level = DuplicateLevel.SamePhoto;
    private bool _loadInProgress;

    public ObservableCollection<LevelChip> Levels { get; } =
    [
        new() { Level = DuplicateLevel.Exact, Label = "Identical files" },
        new() { Level = DuplicateLevel.SamePhoto, Label = "Same photo", IsSelected = true },
        new() { Level = DuplicateLevel.Adjusted, Label = "Adjusted" },
        new() { Level = DuplicateLevel.SameMoment, Label = "Same moment" },
        new() { Level = DuplicateLevel.Similar, Label = "Similar shots" },
    ];

    public ObservableCollection<DuplicateGroupCard> Groups { get; } = [];

    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private double scanProgress;
    [ObservableProperty] private string scanStatus = "";
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string levelHint = "";
    [ObservableProperty] private bool hasGroups;
    [ObservableProperty] private string binButtonText = "";
    [ObservableProperty] private bool canBin;

    public async Task LoadAsync()
    {
        if (_loadInProgress || Groups.Count > 0)
            return;

        _loadInProgress = true;
        IsScanning = true;
        try
        {
            var photos = await gallery.GetPhotosAsync();

            // The exact tier keys off byte length, and iOS does not report sizes up front —
            // without this every file would look like a unique length and nothing would be
            // hashed at all.
            await gallery.EnsureSizesAsync(photos);

            // Anything already headed for the bin is out of the conversation.
            var reviews = await gallery.Store.GetAllAsync();
            var binned = reviews
                .Where(r => r.Decision == SwipeDecision.Trash)
                .Select(r => r.PhotoId)
                .ToHashSet(StringComparer.Ordinal);

            var candidates = photos.Where(p => !binned.Contains(p.Id)).ToList();

            var progress = new Progress<ScanProgress>(report =>
            {
                ScanProgress = report.Fraction;
                ScanStatus = report.Total > 1
                    ? $"{report.Stage} · {report.Done:N0} of {report.Total:N0}"
                    : report.Stage;
            });

            _scanned = await scanner.ScanAsync(candidates, progress);

            // All five groupings up front, off the UI thread, so the counts are real and
            // switching level costs nothing afterwards.
            ScanStatus = "Grouping";
            var grouped = await Task.Run(() =>
                AllLevels.ToDictionary(level => level, level => DuplicateGrouper.Group(_scanned, level)));

            _byLevel.Clear();
            foreach (var (level, groups) in grouped)
                _byLevel[level] = groups;

            foreach (var chip in Levels)
            {
                var groups = _byLevel[chip.Level];
                int redundant = groups.Sum(g => g.RedundantCount);
                chip.CountText = redundant == 0
                    ? "none"
                    : $"{redundant:N0} · {ByteFormat.Human(groups.Sum(g => g.ReclaimableBytes))}";
            }

            Render();
        }
        finally
        {
            IsScanning = false;
            _loadInProgress = false;
        }
    }

    private void Render()
    {
        var rules = DuplicateRules.For(_level);
        var groups = _byLevel.GetValueOrDefault(_level) ?? [];

        Groups.Clear();
        foreach (var group in groups)
            Groups.Add(BuildCard(group, rules.PreselectsKeeper));

        HasGroups = Groups.Count > 0;
        LevelHint = HintFor(_level);

        int redundant = groups.Sum(g => g.RedundantCount);
        SummaryText = redundant == 0
            ? "Nothing to clean up here"
            : $"{groups.Count:N0} group{(groups.Count == 1 ? "" : "s")} · " +
              $"{redundant:N0} extra copies · up to {ByteFormat.Human(groups.Sum(g => g.ReclaimableBytes))}";

        UpdateBinButton();
    }

    private DuplicateGroupCard BuildCard(DuplicateGroup group, bool preselect)
    {
        var items = new ObservableCollection<DuplicateItem>();

        foreach (var photo in group.Items)
        {
            bool isKeeper = photo.Id == group.Keeper.Id;
            var item = new DuplicateItem
            {
                PhotoId = photo.Id,
                MonthKey = photo.MonthKey,
                Thumbnail = gallery.CreateThumbnail(photo.Id, 320),
                DimensionsText = photo.Width > 0 && photo.Height > 0 ? $"{photo.Width}×{photo.Height}" : "",
                SizeText = photo.SizeBytes > 0 ? ByteFormat.Human(photo.SizeBytes) : "",
                LocationText = photo.LocationText,
                SizeBytes = photo.SizeBytes,
                IsKeeper = isKeeper,

                // Only the levels we can stand behind arrive with anything ticked. The looser
                // ones surface the group and leave every choice to the user.
                IsSelected = preselect && !isKeeper,
            };
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(DuplicateItem.IsSelected))
                    UpdateBinButton();
            };
            items.Add(item);
        }

        var card = new DuplicateGroupCard
        {
            Items = items,
            IsExact = group.IsExact,
            BadgeText = group.IsExact ? "Identical files" : "Looks the same",
            HeaderText = $"{group.Items.Count} copies · {ByteFormat.Human(group.ReclaimableBytes)} recoverable",
        };
        return card;
    }

    private static string HintFor(DuplicateLevel level) => level switch
    {
        DuplicateLevel.Exact =>
            "Byte-for-byte identical files. Nothing here is a judgement call.",
        DuplicateLevel.SamePhoto =>
            "The same picture, re-encoded — what WhatsApp, Bluetooth or email does to a photo. Identical files are included too.",
        DuplicateLevel.Adjusted =>
            "The same picture with a filter, an exposure tweak or a quarter turn. Worth a look before you delete.",
        DuplicateLevel.SameMoment =>
            "Burst shots taken seconds apart. These are genuinely different photos — pick the keeper yourself.",
        DuplicateLevel.Similar =>
            "Similar shots from one session. Meant for browsing what a day produced, not for bulk deleting.",
        _ => "",
    };

    private void UpdateBinButton()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).ToList();
        long bytes = selected.Sum(i => i.SizeBytes);

        CanBin = selected.Count > 0 && !IsBusy;
        BinButtonText = selected.Count == 0
            ? "Nothing selected"
            : $"Move {selected.Count:N0} to bin · free {ByteFormat.Human(bytes)}";
    }

    [RelayCommand]
    private void SelectLevel(LevelChip? chip)
    {
        if (chip is null || chip.Level == _level)
            return;

        _level = chip.Level;
        foreach (var entry in Levels)
            entry.IsSelected = ReferenceEquals(entry, chip);

        Render();
    }

    [RelayCommand]
    private void SelectAllExtras()
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.IsSelected = !item.IsKeeper;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.IsSelected = false;
    }

    [RelayCommand]
    private void Toggle(DuplicateItem? item)
    {
        if (item is not null)
            item.IsSelected = !item.IsSelected;
    }

    /// <summary>Throws the fingerprint cache away and scans from scratch.</summary>
    [RelayCommand]
    private async Task Rescan()
    {
        if (IsBusy || IsScanning)
            return;

        await scanner.ResetAsync();
        _byLevel.Clear();
        Groups.Clear();
        HasGroups = false;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task SendToBin()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).ToList();
        if (selected.Count == 0 || IsBusy)
            return;

        long bytes = selected.Sum(i => i.SizeBytes);
        bool confirmed = await Shell.Current.DisplayAlertAsync(
            "Move to bin",
            $"Move {selected.Count:N0} item{(selected.Count == 1 ? "" : "s")} to the bin, freeing about " +
            $"{ByteFormat.Human(bytes)}?\n\nNothing leaves your gallery until you empty the bin.",
            "Move to bin", "Cancel");
        if (!confirmed)
            return;

        IsBusy = true;
        try
        {
            foreach (var item in selected)
            {
                await gallery.Store.UpsertAsync(new ReviewRecord
                {
                    PhotoId = item.PhotoId,
                    MonthKey = item.MonthKey,
                    Decision = SwipeDecision.Trash,
                    DecidedAt = DateTime.UtcNow,
                    SizeBytes = item.SizeBytes,
                });
            }

            // Those items are spoken for now, so drop them and re-derive the groups from
            // what is left — a group of two that loses one is no longer a group.
            var binned = selected.Select(i => i.PhotoId).ToHashSet(StringComparer.Ordinal);
            _scanned = [.. _scanned.Where(s => !binned.Contains(s.Photo.Id))];

            var grouped = await Task.Run(() =>
                AllLevels.ToDictionary(level => level, level => DuplicateGrouper.Group(_scanned, level)));

            _byLevel.Clear();
            foreach (var (level, groups) in grouped)
                _byLevel[level] = groups;

            foreach (var chip in Levels)
            {
                var groups = _byLevel[chip.Level];
                int redundant = groups.Sum(g => g.RedundantCount);
                chip.CountText = redundant == 0
                    ? "none"
                    : $"{redundant:N0} · {ByteFormat.Human(groups.Sum(g => g.ReclaimableBytes))}";
            }

            Render();
        }
        finally
        {
            IsBusy = false;
            UpdateBinButton();
        }
    }
}
