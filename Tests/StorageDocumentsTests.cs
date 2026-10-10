using SortAndDelete.Logic;
using Xunit;

namespace SortAndDelete.Tests;

public class StorageDocumentsTests
{
    private const string InternalStorage = "/storage/emulated/0";

    [Theory]
    [InlineData("/storage/emulated/0/DCIM/Camera/IMG_1.jpg", "primary:DCIM/Camera/IMG_1.jpg")]
    [InlineData("/storage/emulated/0/Pictures/My Trip/IMG 1.jpg", "primary:Pictures/My Trip/IMG 1.jpg")]
    [InlineData("/storage/emulated/0/IMG_1.jpg", "primary:IMG_1.jpg")]
    [InlineData("/storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg",
        "primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg")]
    // links to internal storage, as a legacy app may have stored the path
    [InlineData("/sdcard/DCIM/Camera/IMG_1.jpg", "primary:DCIM/Camera/IMG_1.jpg")]
    [InlineData("/sdcard/IMG_1.jpg", "primary:IMG_1.jpg")]
    [InlineData("/storage/self/primary/DCIM/IMG_1.jpg", "primary:DCIM/IMG_1.jpg")]
    // an SD card keeps its name as mounted
    [InlineData("/storage/1103-3D09/DCIM/Camera/IMG_1.jpg", "1103-3D09:DCIM/Camera/IMG_1.jpg")]
    [InlineData("/storage/1103-3D09/IMG_1.jpg", "1103-3D09:IMG_1.jpg")]
    public void A_file_is_a_document_of_its_volume(string path, string expected) =>
        Assert.Equal(expected, StorageDocuments.DocumentIdOfPath(path, InternalStorage));

    [Theory]
    [InlineData("/storage/emulated/10")]
    [InlineData("/storage/emulated/10/")]
    public void Internal_storage_is_where_it_is_mounted_for_the_user(string root) =>
        Assert.Equal("primary:DCIM/IMG_1.jpg", StorageDocuments.DocumentIdOfPath("/storage/emulated/10/DCIM/IMG_1.jpg", root));

    [Theory]
    // the top of a volume is no file
    [InlineData("/storage/emulated/0")]
    [InlineData("/storage/emulated/0/")]
    [InlineData("/sdcard/")]
    [InlineData("/storage/self/primary")]
    [InlineData("/storage/1103-3D09")]
    [InlineData("/storage/1103-3D09/")]
    // another user's storage, and places the provider doesn't serve
    [InlineData("/storage/emulated/10/DCIM/IMG_1.jpg")]
    [InlineData("/storage/self/DCIM/IMG_1.jpg")]
    [InlineData("/storage/emulated/0DCIM/IMG_1.jpg")]
    [InlineData("/data/media/0/DCIM/IMG_1.jpg")]
    [InlineData("/storage//DCIM/IMG_1.jpg")]
    [InlineData("")]
    // stepping out of a folder
    [InlineData("/storage/emulated/0/DCIM/../IMG_1.jpg")]
    public void Elsewhere_a_file_is_no_document(string path) =>
        Assert.Null(StorageDocuments.DocumentIdOfPath(path, InternalStorage));

    [Theory]
    [InlineData("primary:DCIM/Camera/IMG_1.jpg", "primary:DCIM")]
    [InlineData("primary:DCIM/IMG_1.jpg", "primary:DCIM")]
    [InlineData("primary:Bluetooth/2024/IMG_1.jpg", "primary:Bluetooth")]
    [InlineData("1103-3D09:DCIM/Camera/IMG_1.jpg", "1103-3D09:DCIM")]
    // at the top of a volume: the top
    [InlineData("primary:IMG_1.jpg", "primary:")]
    [InlineData("1103-3D09:IMG_1.jpg", "1103-3D09:")]
    // under Android/, which holds every app's folders: the file's own folder, never one above it
    [InlineData("primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg",
        "primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images")]
    [InlineData("1103-3D09:android/media/org.telegram.messenger/Telegram/IMG_1.jpg",
        "1103-3D09:android/media/org.telegram.messenger/Telegram")]
    [InlineData("primary:Android/IMG_1.jpg", "primary:Android")]
    public void The_folder_asked_for_is_the_top_level_one(string document, string folder) =>
        Assert.Equal(folder, StorageDocuments.FolderToGrant(document));

    [Theory]
    [InlineData("primary:")]
    [InlineData("1103-3D09:")]
    [InlineData("not-a-document-id")]
    public void No_folder_is_asked_for_without_a_file(string document) =>
        Assert.Null(StorageDocuments.FolderToGrant(document));

    [Theory]
    // the one nearest the top, whose grant may cover the others too
    [InlineData(new[] { "primary:DCIM/Camera/IMG_1.jpg", "primary:IMG_2.jpg" }, "primary:")]
    [InlineData(new[] { "primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg",
        "primary:Pictures/Trips/IMG_2.jpg" }, "primary:Pictures")]
    // of those as near, the first
    [InlineData(new[] { "1103-3D09:DCIM/IMG_1.jpg", "primary:Pictures/Trips/IMG_2.jpg" }, "1103-3D09:DCIM")]
    [InlineData(new[] { "primary:Pictures/Trips/IMG_2.jpg", "1103-3D09:DCIM/IMG_1.jpg" }, "primary:Pictures")]
    // documents that name no file are passed over
    [InlineData(new[] { "primary:", "primary:DCIM/IMG_1.jpg" }, "primary:DCIM")]
    public void The_folder_asked_for_first_is_the_one_nearest_the_top(string[] documents, string folder) =>
        Assert.Equal(folder, StorageDocuments.FirstFolderToGrant(documents));

    [Fact]
    public void No_folder_is_asked_for_first_without_a_file()
    {
        Assert.Null(StorageDocuments.FirstFolderToGrant([]));
        Assert.Null(StorageDocuments.FirstFolderToGrant(["primary:", "not-a-document-id"]));
    }

    [Theory]
    // its folder, or one above it, on the same volume
    [InlineData("primary:DCIM", "primary:DCIM/IMG_1.jpg", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:DCIM", "primary:DCIM/Camera/IMG_1.jpg", "primary:DCIM/Camera/IMG_1.jpg")]
    [InlineData("primary:DCIM/Camera", "primary:DCIM/Camera/IMG_1.jpg", "primary:DCIM/Camera/IMG_1.jpg")]
    [InlineData("primary:", "primary:DCIM/Camera/IMG_1.jpg", "primary:DCIM/Camera/IMG_1.jpg")]
    [InlineData("1103-3D09:DCIM", "1103-3D09:DCIM/Camera/IMG_1.jpg", "1103-3D09:DCIM/Camera/IMG_1.jpg")]
    // files at the top of a volume, under its top
    [InlineData("primary:", "primary:IMG_1.jpg", "primary:IMG_1.jpg")]
    [InlineData("1103-3D09:", "1103-3D09:IMG_1.jpg", "1103-3D09:IMG_1.jpg")]
    // spelled differently: what the grant covers in the grant's spelling, the rest in the file's
    [InlineData("primary:dcim", "primary:DCIM/Camera/IMG_1.jpg", "primary:dcim/Camera/IMG_1.jpg")]
    [InlineData("primary:DCIM/camera", "primary:dcim/Camera/IMG_1.jpg", "primary:DCIM/camera/IMG_1.jpg")]
    [InlineData("1103-3d09:DCIM", "1103-3D09:dcim/IMG_1.jpg", "1103-3d09:DCIM/IMG_1.jpg")]
    // an app's folder
    [InlineData("primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images",
        "primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg",
        "primary:Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg")]
    public void A_grant_covers_the_documents_in_its_folder(string tree, string document, string expected) =>
        Assert.Equal(expected, StorageDocuments.DocumentIdInGrant(tree, document));

    [Theory]
    // another volume
    [InlineData("primary:DCIM", "1103-3D09:DCIM/IMG_1.jpg")]
    [InlineData("1103-3D09:DCIM", "primary:DCIM/IMG_1.jpg")]
    [InlineData("1103-3D09:", "5E6F-7A8B:DCIM/IMG_1.jpg")]
    [InlineData("primary:", "1103-3D09:IMG_1.jpg")]
    // another folder, one inside the file's folder, or part of a name
    [InlineData("primary:Pictures", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:DCIM/Camera", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:DC", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:DCIM", "primary:IMG_1.jpg")]
    // another app's folder
    [InlineData("primary:Android/media/org.telegram.messenger", "primary:Android/media/com.whatsapp/IMG-1.jpg")]
    // no file inside it
    [InlineData("primary:DCIM/IMG_1.jpg", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:", "primary:")]
    [InlineData("not-a-document-id", "primary:DCIM/IMG_1.jpg")]
    [InlineData("primary:DCIM", "not-a-document-id")]
    // a path that steps out of a folder
    [InlineData("primary:DCIM", "primary:DCIM/../Pictures/IMG_1.jpg")]
    [InlineData("primary:DCIM", "primary:DCIM/./IMG_1.jpg")]
    [InlineData("home:", "primary:Documents/../DCIM/IMG_1.jpg")]
    [InlineData("primary:", "primary:../IMG_1.jpg")]
    public void A_grant_does_not_cover_documents_elsewhere(string tree, string document) =>
        Assert.Null(StorageDocuments.DocumentIdInGrant(tree, document));

    [Theory]
    [InlineData("/storage/emulated/0/DCIM/Camera/IMG_1.jpg")]
    [InlineData("/storage/emulated/0/IMG_1.jpg")]
    [InlineData("/sdcard/DCIM/Camera/IMG_1.jpg")]
    [InlineData("/storage/1103-3D09/Pictures/Trips/IMG_1.jpg")]
    [InlineData("/storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg")]
    public void The_folder_asked_for_covers_the_file(string path)
    {
        var document = StorageDocuments.DocumentIdOfPath(path, InternalStorage)!;
        var folder = StorageDocuments.FolderToGrant(document)!;

        Assert.Equal(document, StorageDocuments.DocumentIdInGrant(folder, document));
    }

    // Android 10: asked for "primary:Documents", the folder picker opens on Documents and answers
    // with the provider's Documents root, "home:", or "home:Scans" for a folder inside it.

    [Theory]
    [InlineData("home:", "primary:Documents/B29A5/b29_a5_item.jpg", "home:B29A5/b29_a5_item.jpg")]
    [InlineData("home:", "primary:Documents/IMG_1.jpg", "home:IMG_1.jpg")]
    [InlineData("home:B29A5", "primary:Documents/B29A5/b29_a5_item.jpg", "home:B29A5/b29_a5_item.jpg")]
    [InlineData("home:B29A5", "primary:Documents/B29A5/2024/IMG_1.jpg", "home:B29A5/2024/IMG_1.jpg")]
    // spelled differently: what the grant covers in the grant's spelling, the rest in the file's
    [InlineData("home:", "primary:documents/Scans/IMG_1.jpg", "home:Scans/IMG_1.jpg")]
    [InlineData("home:scans", "primary:Documents/Scans/IMG_1.jpg", "home:scans/IMG_1.jpg")]
    [InlineData("home:", "PRIMARY:Documents/IMG_1.jpg", "home:IMG_1.jpg")]
    // a file named in the Documents root itself, under either kind of grant
    [InlineData("home:Scans", "home:Scans/IMG_1.jpg", "home:Scans/IMG_1.jpg")]
    [InlineData("primary:", "home:Scans/IMG_1.jpg", "primary:Documents/Scans/IMG_1.jpg")]
    [InlineData("primary:Documents", "home:Scans/IMG_1.jpg", "primary:Documents/Scans/IMG_1.jpg")]
    public void A_grant_of_the_Documents_root_covers_the_files_in_Documents(string tree, string document, string expected) =>
        Assert.Equal(expected, StorageDocuments.DocumentIdInGrant(tree, document));

    [Theory]
    // not the top of the storage: nothing outside Documents
    [InlineData("home:", "primary:DCIM/IMG_1.jpg")]
    [InlineData("home:", "primary:IMG_1.jpg")]
    [InlineData("home:", "primary:Documents2/IMG_1.jpg")]
    // an SD card has no Documents root: its Documents folder is "1103-3D09:Documents"
    [InlineData("home:", "1103-3D09:Documents/IMG_1.jpg")]
    // another folder inside Documents (the "wrong folder" a user can pick), or one inside the file's
    [InlineData("home:B29Sub", "primary:Documents/B29A5/b29_a5_item.jpg")]
    [InlineData("home:B29A5/2024", "primary:Documents/B29A5/b29_a5_item.jpg")]
    [InlineData("home:B29", "primary:Documents/B29A5/b29_a5_item.jpg")]
    // no file inside it
    [InlineData("home:", "primary:Documents")]
    [InlineData("home:", "home:")]
    public void A_grant_of_the_Documents_root_covers_nothing_else(string tree, string document) =>
        Assert.Null(StorageDocuments.DocumentIdInGrant(tree, document));

    [Theory]
    [InlineData("/storage/emulated/0/Documents/B29A5/b29_a5_item.jpg", "home:")]
    [InlineData("/storage/emulated/0/Documents/B29A5/b29_a5_item.jpg", "home:B29A5")]
    [InlineData("/sdcard/Documents/IMG_1.jpg", "home:")]
    public void What_Android_10_grants_for_Documents_covers_the_file_it_was_asked_for(string path, string granted)
    {
        var document = StorageDocuments.DocumentIdOfPath(path, InternalStorage)!;

        // The folder asked for is still "primary:Documents": the picker opens on Documents from it.
        Assert.Equal("primary:Documents", StorageDocuments.FolderToGrant(document));
        Assert.StartsWith("home:", StorageDocuments.DocumentIdInGrant(granted, document));
    }
}
