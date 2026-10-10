using SortAndDelete.Models;

namespace SortAndDelete.Logic;

/// <summary>
/// What tells one state of a photo from the next: an edit, or another file under the same id,
/// moves at least one of these.
/// </summary>
public readonly record struct PhotoVersion(long SizeBytes, DateTime ModifiedAt, DateTime FileModifiedAt, bool IsEdited)
{
    public static PhotoVersion Of(PhotoAsset photo) =>
        new(photo.SizeBytes, photo.ModifiedAt, photo.FileModifiedAt, photo.IsEdited);
}

/// <summary>
/// One copy in a group on screen: the photo as it was then, and whether it was ticked for the bin.
/// </summary>
public sealed record ItemTick(string PhotoId, PhotoVersion Version, bool IsTicked);

/// <summary>A group on screen, in the order of <see cref="DuplicateGroup.Items"/>.</summary>
public sealed record GroupTicks(string KeeperId, IReadOnlyList<ItemTick> Items);

/// <summary>
/// Which copies are ticked for the bin as groups go on screen.
///
/// Left to itself a level ticks by its own rule. But groups are rebuilt by every rescan and
/// regroup, which can be no more than the user looking at a picture in another app and coming
/// back, and what they ticked and unticked meanwhile mustn't be lost to that. So:
///
/// <list type="bullet">
/// <item>a tick is carried over when its group came back as it was, with the same members and
/// the same one to keep, and neither its own photo nor the one to keep has changed. What was
/// ticked was that picture among those copies, to go while that one stays: with the keeper
/// edited meanwhile, the copy ticked beside it may be the only clean one left;</item>
/// <item>an item left unticked stays unticked for as long as its group has the same keeper,
/// whoever has joined or left the group and whatever has become of the photo. Unticked is the
/// safe side, and a level that ticks by itself would otherwise tick it again on every rescan;</item>
/// <item>anything else gets the level's own. That goes for the whole of a group with another
/// keeper: the one it had was unticked for being the keeper, not by anyone's choice.</item>
/// </list>
/// </summary>
public static class TickCarryOver
{
    /// <summary>
    /// The ticks for <paramref name="groups"/>, group by group and item by item in their order.
    /// </summary>
    /// <param name="before">The groups these replace as they last stood on screen, if any.</param>
    /// <param name="groups">The groups going on screen.</param>
    /// <param name="preselect">Whether the level ticks everything but the keeper (<see cref="DuplicateRules.PreselectsKeeper"/>).</param>
    public static List<GroupTicks> Apply(
        IReadOnlyList<GroupTicks>? before, IReadOnlyList<DuplicateGroup> groups, bool preselect)
    {
        var was = new Dictionary<string, (GroupTicks Group, ItemTick Item)>(StringComparer.Ordinal);
        foreach (var group in before ?? [])
        {
            foreach (var item in group.Items)
                was[item.PhotoId] = (group, item);
        }

        return [.. groups.Select(group =>
        {
            bool cameBackAsItWas =
                was.TryGetValue(group.Keeper.Id, out var keeper)
                && keeper.Group.KeeperId == group.Keeper.Id
                && keeper.Group.Items.Count == group.Items.Count
                && keeper.Item.Version == PhotoVersion.Of(group.Keeper)
                && group.Items.All(photo =>
                    was.TryGetValue(photo.Id, out var member) && ReferenceEquals(member.Group, keeper.Group));

            return new GroupTicks(group.Keeper.Id, [.. group.Items.Select(photo =>
            {
                var version = PhotoVersion.Of(photo);
                bool ticked = TickedByDefault(group, photo, preselect);

                // Nothing is carried from under another keeper. The ticks a level put there
                // followed from which copy it suggested keeping, and so did that copy's being
                // unticked; carried over to another suggestion, they would leave the copy now
                // marked to keep ticked for the bin and the old one out of it.
                if (was.TryGetValue(photo.Id, out var last) && last.Group.KeeperId == group.Keeper.Id)
                {
                    if (!last.Item.IsTicked)
                        ticked = false;
                    else if (cameBackAsItWas && last.Item.Version == version)
                        ticked = true;
                }

                return new ItemTick(photo.Id, version, ticked);
            })]);
        })];
    }

    /// <summary>
    /// Only the levels we can stand behind arrive with anything ticked. The looser ones surface
    /// the group and leave every choice to the user. An edited item never does: ranking can't
    /// tell it from its original, so which of the two would go comes down to their ids. Nor does
    /// one that only looks like another of its own size
    /// (<see cref="DuplicateGroup.SameSizeLookalikes"/>): that much a different picture can do.
    /// </summary>
    private static bool TickedByDefault(DuplicateGroup group, PhotoAsset photo, bool preselect) =>
        preselect && photo.Id != group.Keeper.Id && !photo.IsEdited
        && !group.SameSizeLookalikes.Contains(photo.Id);
}
