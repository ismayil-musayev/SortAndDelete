using SortAndDelete.Logic;
using SortAndDelete.Models;
using Xunit;

namespace SortAndDelete.Tests;

public class MoveRoutingTests
{
    private const bool Android11 = true;
    private const bool Android10 = false;

    [Theory]
    [InlineData("DCIM/Camera/")]
    [InlineData("Pictures/")]
    [InlineData("Pictures/Travel/2024/")]
    public void Images_move_in_place_under_DCIM_and_Pictures(string folder)
    {
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor(folder, MediaKind.Image, Android11));
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor(folder, MediaKind.Image, Android10));
    }

    [Theory]
    [InlineData("DCIM/Camera/")]
    [InlineData("Movies/")]
    public void Videos_move_in_place_under_DCIM_and_Movies(string folder)
    {
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor(folder, MediaKind.Video, Android11));
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor(folder, MediaKind.Video, Android10));
    }

    [Fact]
    public void Videos_move_in_place_under_Pictures_only_from_Android_11()
    {
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor("Pictures/Docs/", MediaKind.Video, Android11));
        Assert.Equal(MoveRoute.FolderAccess, MoveRouting.RouteFor("Pictures/Docs/", MediaKind.Video, Android10));
    }

    [Fact]
    public void An_image_cannot_move_in_place_into_Movies() =>
        Assert.Equal(MoveRoute.FolderAccess, MoveRouting.RouteFor("Movies/", MediaKind.Image, Android11));

    [Theory]
    [InlineData("pictures/travel/", MoveRoute.InPlace)]
    [InlineData("dcim/", MoveRoute.InPlace)]
    [InlineData("download/", MoveRoute.Copy)]
    [InlineData("DOCUMENTS/scans/", MoveRoute.Copy)]
    public void Standard_folders_route_whatever_their_spelling(string folder, MoveRoute route) =>
        Assert.Equal(route, MoveRouting.RouteFor(folder, MediaKind.Image, Android11));

    [Theory]
    [InlineData("pictures/travel/", "Pictures/travel/")]
    [InlineData("download/", "Download/")]
    [InlineData("Pictures/Travel/", "Pictures/Travel/")]
    [InlineData("Bluetooth/", "Bluetooth/")]
    [InlineData("/", "/")]
    public void Canonical_spelling_fixes_only_a_standard_top_folder(string folder, string expected) =>
        Assert.Equal(expected, MoveRouting.Canonical(folder));

    [Theory]
    [InlineData("Android/media/com.whatsapp/", true)]
    [InlineData("android/data/", true)]
    [InlineData("Androids/", false)]
    [InlineData("Pictures/Android/", false)]
    [InlineData("/", false)]
    public void App_folders_are_everything_under_Android(string folder, bool isAppFolder) =>
        Assert.Equal(isAppFolder, MoveRouting.IsAppFolder(folder));

    [Theory]
    [InlineData("primary:Android/media/com.whatsapp/WhatsApp/Media", true)]
    [InlineData("1A2B-3C4D:Android/media/org.telegram", true)]
    [InlineData("primary:Bluetooth", false)]
    [InlineData("primary:", false)]
    public void Browsed_app_folders_are_recognised(string tree, bool isAppFolder) =>
        Assert.Equal(isAppFolder, MoveRouting.IsAppFolderGrant(tree));

    [Theory]
    [InlineData("Download/", MediaKind.Image)]
    [InlineData("Download/Telegram/", MediaKind.Video)]
    [InlineData("Documents/", MediaKind.Image)]
    [InlineData("Documents/Scans/", MediaKind.Video)]
    public void Download_and_Documents_take_a_copy(string folder, MediaKind kind) =>
        Assert.Equal(MoveRoute.Copy, MoveRouting.RouteFor(folder, kind, Android11));

    [Theory]
    [InlineData("Bluetooth/")]
    [InlineData("MyAlbum/")]
    [InlineData("MyAlbum/Sub/")]
    public void Other_folders_need_the_folder_picker(string folder) =>
        Assert.Equal(MoveRoute.FolderAccess, MoveRouting.RouteFor(folder, MediaKind.Image, Android11));

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("Android/")]
    [InlineData("Android/data/com.example/files/")]
    [InlineData("Android/media/")]
    [InlineData("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/")]
    [InlineData("android/media/com.whatsapp/")]
    public void The_storage_root_and_app_folders_are_never_targets(string folder) =>
        Assert.Equal(MoveRoute.NotPossible, MoveRouting.RouteFor(folder, MediaKind.Image, Android11));

    [Fact]
    public void Folders_without_asking_follow_the_same_rules()
    {
        Assert.Equal(["DCIM", "Pictures", "Download", "Documents"],
            MoveRouting.FoldersWithoutAsking(MediaKind.Image, Android11));
        Assert.Equal(["DCIM", "Movies", "Pictures", "Download", "Documents"],
            MoveRouting.FoldersWithoutAsking(MediaKind.Video, Android11));
        Assert.Equal(["DCIM", "Movies", "Download", "Documents"],
            MoveRouting.FoldersWithoutAsking(MediaKind.Video, Android10));
    }

    [Theory]
    [InlineData(MediaKind.Image, Android11, "Pictures")]
    [InlineData(MediaKind.Image, Android10, "Pictures")]
    [InlineData(MediaKind.Video, Android11, "Pictures")]
    [InlineData(MediaKind.Video, Android10, "Movies")]
    public void New_albums_go_where_the_move_is_in_place(MediaKind kind, bool videosInPictures, string root)
    {
        Assert.Equal(root, MoveRouting.NewAlbumRoot(kind, videosInPictures));
        Assert.Equal(MoveRoute.InPlace, MoveRouting.RouteFor($"{root}/Travel/", kind, videosInPictures));
    }

    [Theory]
    [InlineData("Bluetooth/", "Bluetooth/", true)]
    [InlineData("Bluetooth/", "bluetooth", true)]
    [InlineData("DCIM/Camera/", "DCIM/Camera/", true)]
    [InlineData("DCIM/Camera/", "DCIM/", false)]
    [InlineData("Pictures/Docs/", "Documents/", false)]
    public void Same_folder_ignores_case_and_slashes(string a, string b, bool same) =>
        Assert.Equal(same, MoveRouting.IsSameFolder(a, b));

    [Theory]
    [InlineData("primary:Bluetooth", "Bluetooth/", true)]
    [InlineData("primary:Bluetooth", "Bluetooth/2024/", true)]
    [InlineData("primary:bluetooth", "Bluetooth/", true)]
    [InlineData("1A2B-3C4D:Bluetooth", "Bluetooth/", true)]
    [InlineData("primary:", "Bluetooth/", true)]
    [InlineData("primary:Bluetooth/2024", "Bluetooth/", false)]
    [InlineData("primary:Blue", "Bluetooth/", false)]
    [InlineData("primary:MyAlbum", "Bluetooth/", false)]
    [InlineData("not-a-document-id", "Bluetooth/", false)]
    public void A_grant_covers_its_folder_and_everything_inside(string tree, string folder, bool covers) =>
        Assert.Equal(covers, MoveRouting.GrantCovers(tree, folder));

    [Theory]
    [InlineData("primary:Bluetooth", "Bluetooth/", "primary:Bluetooth")]
    [InlineData("primary:Bluetooth", "Bluetooth/2024/", "primary:Bluetooth/2024")]
    [InlineData("primary:bluetooth", "Bluetooth/2024/", "primary:bluetooth/2024")]
    [InlineData("1A2B-3C4D:Media", "Media/Old/", "1A2B-3C4D:Media/Old")]
    [InlineData("primary:", "Bluetooth/", "primary:Bluetooth")]
    public void The_folder_document_keeps_the_grants_spelling(string tree, string folder, string expected) =>
        Assert.Equal(expected, MoveRouting.FolderDocumentId(tree, folder));

    [Fact]
    public void No_folder_document_outside_the_grant() =>
        Assert.Null(MoveRouting.FolderDocumentId("primary:MyAlbum", "Bluetooth/"));

    [Theory]
    [InlineData("primary:MyAlbum", "MyAlbum/", true)]
    [InlineData("primary:MyAlbum", "MyAlbum/Sub/", false)]
    [InlineData("primary:Documents", "Documents/", true)]
    [InlineData("primary:Documents", "Download/", false)]
    [InlineData("primary:", "/", true)]
    public void A_grant_for_a_folder_means_exactly_that_folder(string tree, string folder, bool same) =>
        Assert.Equal(same, MoveRouting.IsGrantFor(tree, folder));

    [Fact]
    public void In_place_folders_are_the_ones_moves_need_no_copy_for()
    {
        Assert.Equal(["DCIM", "Pictures"], MoveRouting.InPlaceFoldersFor(MediaKind.Image, Android11));
        Assert.Equal(["DCIM", "Movies", "Pictures"], MoveRouting.InPlaceFoldersFor(MediaKind.Video, Android11));
        Assert.Equal(["DCIM", "Movies"], MoveRouting.InPlaceFoldersFor(MediaKind.Video, Android10));
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "DCIM" }, "DCIM")]
    [InlineData(new[] { "DCIM", "Pictures" }, "DCIM and Pictures")]
    [InlineData(new[] { "DCIM", "Movies", "Pictures" }, "DCIM, Movies and Pictures")]
    public void Folder_names_read_as_a_phrase(string[] names, string expected) =>
        Assert.Equal(expected, MoveRouting.InWords(names));

    [Theory]
    [InlineData("primary:Pictures/Trips", "Pictures/Trips/")]
    [InlineData("primary:Bluetooth", "Bluetooth/")]
    [InlineData("1A2B-3C4D:DCIM/Camera/", "DCIM/Camera/")]
    [InlineData("primary:", "")]
    [InlineData("1A2B-3C4D:", "")]
    [InlineData("not-a-document-id", "")]
    public void A_grant_names_its_folder_by_relative_path(string tree, string expected) =>
        Assert.Equal(expected, MoveRouting.RelativePathOfGrant(tree));

    [Theory]
    [InlineData("primary:DCIM", "external_primary", true)]
    [InlineData("primary:DCIM", "external", true)]
    [InlineData("primary:DCIM", "1a2b-3c4d", false)]
    [InlineData("1A2B-3C4D:DCIM", "1a2b-3c4d", true)]
    [InlineData("1A2B-3C4D:DCIM", "external_primary", false)]
    [InlineData("1A2B-3C4D:DCIM", "5e6f-7a8b", false)]
    [InlineData("not-a-document-id", "external_primary", false)]
    public void Grant_volumes_match_MediaStore_volumes(string tree, string volume, bool same) =>
        Assert.Equal(same, MoveRouting.IsSameVolume(tree, volume));

    private const string Internal = "external_primary";
    private const string SdCard = "1a2b-3c4d";

    [Theory]
    // never the top of a volume (Android 10's picker offers it) or an app's folder
    [InlineData("primary:", Internal, "DCIM/Camera/", MediaKind.Image, Android10, BrowsedRoute.NotPossible)]
    [InlineData("1A2B-3C4D:", SdCard, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.NotPossible)]
    [InlineData("primary:Android/media/com.whatsapp", Internal, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.NotPossible)]
    [InlineData("not-a-document-id", Internal, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.NotPossible)]
    // the folder it is in already, on the same volume, however it is spelled
    [InlineData("primary:Pictures/Trips", Internal, "Pictures/Trips/", MediaKind.Image, Android11, BrowsedRoute.AlreadyThere)]
    [InlineData("primary:bluetooth", Internal, "Bluetooth/", MediaKind.Image, Android11, BrowsedRoute.AlreadyThere)]
    [InlineData("1A2B-3C4D:DCIM/Camera", SdCard, "DCIM/Camera/", MediaKind.Image, Android11, BrowsedRoute.AlreadyThere)]
    // in place where MediaStore takes the item, within its own volume
    [InlineData("primary:Pictures/Trips", Internal, "DCIM/Camera/", MediaKind.Image, Android11, BrowsedRoute.InPlace)]
    [InlineData("primary:pictures/trips", Internal, "DCIM/Camera/", MediaKind.Image, Android10, BrowsedRoute.InPlace)]
    [InlineData("primary:Movies", Internal, "DCIM/Camera/", MediaKind.Video, Android10, BrowsedRoute.InPlace)]
    [InlineData("primary:Pictures/Clips", Internal, "DCIM/Camera/", MediaKind.Video, Android11, BrowsedRoute.InPlace)]
    [InlineData("1A2B-3C4D:Pictures/Trips", SdCard, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.InPlace)]
    // a MediaStore copy onto internal storage from elsewhere
    [InlineData("primary:Pictures/Trips", SdCard, "Pictures/Trips/", MediaKind.Image, Android11, BrowsedRoute.MediaCopy)]
    [InlineData("primary:Pictures", Internal, "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/", MediaKind.Image, Android11, BrowsedRoute.MediaCopy)]
    [InlineData("primary:Download", Internal, "DCIM/Camera/", MediaKind.Image, Android10, BrowsedRoute.FilesCopy)]
    [InlineData("primary:Documents/Scans", SdCard, "DCIM/", MediaKind.Video, Android11, BrowsedRoute.FilesCopy)]
    // everything else through the folder grant
    [InlineData("primary:Bluetooth", Internal, "DCIM/Camera/", MediaKind.Image, Android11, BrowsedRoute.FolderCopy)]
    [InlineData("primary:Movies", Internal, "DCIM/Camera/", MediaKind.Image, Android11, BrowsedRoute.FolderCopy)]
    [InlineData("primary:Pictures/Clips", Internal, "DCIM/Camera/", MediaKind.Video, Android10, BrowsedRoute.FolderCopy)]
    [InlineData("1A2B-3C4D:Pictures/Trips", Internal, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.FolderCopy)]
    [InlineData("1A2B-3C4D:Download", SdCard, "DCIM/", MediaKind.Image, Android11, BrowsedRoute.FolderCopy)]
    [InlineData("1A2B-3C4D:DCIM/Camera", Internal, "DCIM/Camera/", MediaKind.Image, Android11, BrowsedRoute.FolderCopy)]
    public void Browsed_folders_follow_the_gallery_rules(string tree, string sourceVolume, string sourceFolder,
        MediaKind kind, bool videosInPictures, BrowsedRoute expected) =>
        Assert.Equal(expected, MoveRouting.RouteForBrowsed(tree, sourceVolume, sourceFolder, kind, videosInPictures));
}
