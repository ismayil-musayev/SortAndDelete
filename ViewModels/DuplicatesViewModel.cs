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

    /// <summary>Carries an edit (iOS), so the app never ticks it for the bin by itself.</summary>
    public bool IsEdited { get; init; }

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
/// Scanning happens once, and again only when the library has been reloaded or something has
/// come back out of the bin; the five levels are queries over what it produced, so switching
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

    /// <summary>The load under way, if any. Only one runs at a time.</summary>
    private Task _load = Task.CompletedTask;

    /// <summary>The visit the page is on now, which ↻ scans under.</summary>
    private CancellationToken _visit;

    /// <summary>
    /// The photo list the groups were found in, null while there are none to keep. Its instance
    /// is the test: the gallery hands out the same one until it reloads the library.
    /// </summary>
    private IReadOnlyList<PhotoAsset>? _resultsFrom;

    /// <summary>What was headed for the bin when they were found, plus what went there since.</summary>
    private HashSet<string> _resultsBinned = [];

    /// <summary>
    /// Counts the times the groups on screen were replaced or thrown away, so a move to the bin
    /// started on them can tell.
    /// </summary>
    private int _groupings;

    public ObservableCollection<LevelChip> Levels { get; } =
    [
        new() { Level = DuplicateLevel.Exact, Label = "Identical files" },
        new() { Level = DuplicateLevel.SamePhoto, Label = "Same photo", IsSelected = true },
        new() { Level = DuplicateLevel.Adjusted, Label = "Adjusted" },
        new() { Level = DuplicateLevel.SameMoment, Label = "Same moment" },
        new() { Level = DuplicateLevel.Similar, Label = "Similar shots" },
    ];

    public ObservableCollection<DuplicateGroupCard> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResults))]
    private bool isScanning;

    /// <summary>The last scan ended in an error; <see cref="ScanStatus"/> says so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResults))]
    private bool scanFailed;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private double scanProgress;
    [ObservableProperty] private string scanStatus = "";
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty] private string levelHint = "";
    [ObservableProperty] private bool hasGroups;
    [ObservableProperty] private string binButtonText = "";
    [ObservableProperty] private bool canBin;

    /// <summary>
    /// Whether the groups, or the word that this level has none, are on screen: not while the
    /// scan's progress or its failure stands in for them.
    /// </summary>
    public bool ShowsResults => !IsScanning && !ScanFailed;

    /// <summary>
    /// Brings the page up to date for a visit. Cancelling <paramref name="visit"/>, as the page
    /// does when the user leaves, stops the scan; what it has read is kept, and the next visit
    /// carries on from there.
    /// </summary>
    public Task LoadAsync(CancellationToken visit)
    {
        _visit = visit;
        return LoadAsync(fromScratch: false, visit);
    }

    private async Task LoadAsync(bool fromScratch, CancellationToken visit)
    {
        // One load at a time. One still running may be an ended visit's, saving what it read, or
        // this visit's own: wait it out and look again, unless this visit has ended meanwhile.
        while (!_load.IsCompleted)
            await _load;

        if (visit.IsCancellationRequested)
            return;

        _load = RefreshAsync(fromScratch, visit);
        await _load;
    }

    /// <summary>
    /// Scans, unless the groups on screen still hold. Never throws: it runs under
    /// <c>OnAppearing</c>, an <c>async void</c> that would take the app down with it, so a
    /// failure ends up on the page instead.
    /// </summary>
    private async Task RefreshAsync(bool fromScratch, CancellationToken ct)
    {
        // Groups worth keeping stay up while they're checked; with none, the scan shows at once.
        bool checking = !fromScratch && _resultsFrom is not null;
        try
        {
            if (!checking)
                BeginScan();

            if (fromScratch)
                await scanner.ResetAsync();

            var photos = await gallery.GetPhotosAsync();
            var binned = await GetBinnedAsync();

            if (checking)
            {
                // Moving items to the bin re-derives the groups itself; the next visit catches up.
                if (IsBusy)
                    return;

                // The same list, so not reloaded since, and nothing has left the bin: the groups
                // hold, less whatever went to the bin from elsewhere meanwhile, which a regroup
                // takes out. Something restored needs a scan, as the last one left it out.
                if (ReferenceEquals(photos, _resultsFrom) && binned.IsSupersetOf(_resultsBinned))
                {
                    if (binned.Count > _resultsBinned.Count)
                    {
                        _scanned = [.. _scanned.Where(s => !binned.Contains(s.Photo.Id))];
                        _resultsBinned = binned;
                        await RegroupAsync();
                    }
                    return;
                }

                BeginScan();
            }

            ct.ThrowIfCancellationRequested();

            // The exact tier keys off byte length, and iOS does not report sizes up front —
            // without this every file would look like a unique length and nothing would be
            // hashed at all.
            await gallery.EnsureSizesAsync(photos);
            ct.ThrowIfCancellationRequested();

            var candidates = photos.Where(p => !binned.Contains(p.Id)).ToList();

            var progress = new Progress<ScanProgress>(report =>
            {
                ScanProgress = report.Fraction;
                ScanStatus = report.Total > 1
                    ? $"{report.Stage} · {report.Done:N0} of {report.Total:N0}"
                    : report.Stage;
            });

            _scanned = await scanner.ScanAsync(candidates, progress, ct);
            _resultsFrom = photos;
            _resultsBinned = binned;

            ScanStatus = "Grouping";
            await RegroupAsync();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped by leaving the page. The scan kept what it read; the next visit resumes.
        }
        catch (Exception)
        {
            // Say, a cache that can't be opened on a full disk. The page stays up and says so,
            // with ↻ there to try again.
            DiscardResults();
            ScanStatus = "Something went wrong while scanning. Nothing in your library was " +
                         "changed — tap ↻ to try again, keeping what was already scanned.";
            ScanFailed = true;
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>Takes the groups off screen and shows the scan that will replace them.</summary>
    private void BeginScan()
    {
        DiscardResults();
        ScanFailed = false;
        ScanProgress = 0; // a rescan would otherwise open on the last scan's full bar and "Grouping"
        ScanStatus = "";
        IsScanning = true;
    }

    /// <summary>
    /// Works out every level's groups from <see cref="_scanned"/> and shows the chosen one. All
    /// five up front, off the UI thread, so the counts are real and switching level costs
    /// nothing afterwards.
    /// </summary>
    private async Task RegroupAsync()
    {
        var grouped = await Task.Run(() =>
            AllLevels.ToDictionary(level => level, level => DuplicateGrouper.Group(_scanned, level)));

        _groupings++;
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

    /// <summary>
    /// Forgets the groups and takes them off screen, so nothing ticked in them can reach the bin.
    /// The counts by each level go back to waiting.
    /// </summary>
    private void DiscardResults()
    {
        _groupings++;
        _resultsFrom = null;
        _scanned = [];
        _byLevel.Clear();
        Groups.Clear();
        HasGroups = false;
        SummaryText = "";
        foreach (var chip in Levels)
            chip.CountText = "…";
        UpdateBinButton();
    }

    /// <summary>Everything headed for the bin. It is out of the conversation.</summary>
    private async Task<HashSet<string>> GetBinnedAsync() =>
        (await gallery.Store.GetAllAsync())
            .Where(r => r.Decision == SwipeDecision.Trash)
            .Select(r => r.PhotoId)
            .ToHashSet(StringComparer.Ordinal);

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
        SummaryText = _byLevel.Count == 0
            ? "" // no scan to sum up yet: one is running, or it failed
            : redundant == 0
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
                IsEdited = photo.IsEdited,

                // Only the levels we can stand behind arrive with anything ticked. The looser
                // ones surface the group and leave every choice to the user. An edited item never
                // does: ranking can't tell it from its original, so which of the two would go
                // comes down to their ids.
                IsSelected = preselect && !isKeeper && !photo.IsEdited,
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

    /// <summary>
    /// Throws the fingerprint cache away and scans from scratch. After a failed scan it tries
    /// again instead, keeping the cache: what was read before the failure still holds.
    /// </summary>
    [RelayCommand]
    private async Task Rescan()
    {
        if (IsBusy || IsScanning)
            return;

        await LoadAsync(fromScratch: !ScanFailed, _visit);
    }

    [RelayCommand]
    private async Task SendToBin()
    {
        var selected = Groups.SelectMany(g => g.Items).Where(i => i.IsSelected).ToList();
        if (selected.Count == 0 || IsBusy)
            return;

        long bytes = selected.Sum(i => i.SizeBytes);
        int groupings = _groupings;
        bool confirmed = await Shell.Current.DisplayAlertAsync(
            "Move to bin",
            $"Move {selected.Count:N0} item{(selected.Count == 1 ? "" : "s")} to the bin, freeing about " +
            $"{ByteFormat.Human(bytes)}?\n\nNothing leaves your gallery until you empty the bin.",
            "Move to bin", "Cancel");

        // A load that ran while the question was up has replaced these groups or thrown them
        // away, or may yet, so what was ticked can no longer be taken for what it looked like.
        if (!confirmed || groupings != _groupings || !_load.IsCompleted)
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
            _resultsBinned.UnionWith(binned);
            await RegroupAsync();
        }
        finally
        {
            IsBusy = false;
            UpdateBinButton();
        }
    }
}
