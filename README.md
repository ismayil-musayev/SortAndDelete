# Sort & Delete 📷

A .NET MAUI app for **Android & iOS** that turns gallery cleanup into a quick decision game:
one tap to **keep**, one tap to **delete** — month by month, with a safety net.

## Features

### Core flow
- **Review deck** — card stack with big keep/delete buttons, fly-out animations, haptic feedback,
  **double-tap to zoom**, and **video support** (▶ + duration + file-size badges).
- **Two-stage delete (safety net)** — a left swipe only moves the item to the **in-app bin**.
  Nothing touches your gallery until you *Empty bin*. Restore any item with one tap.
- **System trash with 30-day auto-purge** — emptying the bin moves items to:
  - *Android 11+*: the MediaStore system trash — and Sort & Delete can **restore from it in-app**
    for the whole 30-day window (verified: file renamed to `.trashed-<expiry>-…` and back).
  - *iOS*: the **Recently Deleted** album (restore there via the Photos app — Apple doesn't
    allow apps to do it programmatically).
  - *Android 10*: no system trash exists — the app warns and deletes permanently instead.
    Known limitation: Android 10's MediaStore, asked to delete another app's photo after the
    user's consent, drops the row but can keep the file, which the next media scan brings back.
- **Undo** and a **Later** button for hard choices.

### Progress & resume
- **Months overview** — progress bar, counts and cover thumbnail per month.
- **Continue where you left off** — every decision is stored in SQLite; reviewed photos never
  come back, and the last month you worked on is offered as a "Continue" card.
- **Streak** — 🔥 daily streak + reviewed-today counter.
- **Stats** — total items, % reviewed, storage freed.

### Smart decks
- **Biggest files** 🐘 — largest items first, the fastest way to free space.
- **Duplicates** 🧬 — see [Duplicates](#duplicates) below.

### Albums
- **Move to albums** — send a photo to an existing folder/album ("Docs", "Travel", …) or create
  a new one. Android physically moves the file; iOS adds it to the album (how Photos works).
  Where Android can't move a file in place, the app copies it, reads the copy back to check
  every byte, and only then sends the original to the system trash (Android 11+), so a move is
  recoverable for 30 days. Android 10 can't remove the original after a copy (see above), so
  there moves stay in place: DCIM/Pictures for photos, DCIM/Movies for videos. Known
  limitation: Android 10 refuses even those for items on an SD card ("Changing volume … not
  allowed"), so they can't be moved there; the app says so and nothing changes.

## Duplicates

The case this exists for: you take a photo, send it over WhatsApp, and someone Bluetooths it
back. Three files, three different byte counts, one picture. A checksum sees three unrelated
blobs — WhatsApp re-encodes and resizes, so its copy shares no bytes with the original.

Two tiers, and the first is always on:

1. **Exact copies** — byte-identical files. Only items that already share a byte length get
   hashed, since nothing else can possibly match, which keeps this nearly free.
2. **The same picture** — matched on what it looks like rather than what it contains, so
   resolution, compression and stripped metadata stop mattering.

### The five levels

Each asks a different question and carries its own rule set — they are not one dial at five
settings. Every level includes exact copies. Switching between them re-groups cached
fingerprints, so it is instant and never rescans.

| Level | Finds | Preselects |
|---|---|---|
| Identical files | byte-for-byte the same | yes |
| **Same photo** *(default)* | resized, recompressed, re-sent — WhatsApp, Bluetooth, email | yes |
| Adjusted | filters, exposure, quarter turns, watermarks | no |
| Same moment | burst shots seconds apart | no |
| Similar shots | one session, one scene | no |

Only the levels worth standing behind arrive with anything ticked; the looser ones surface
groups and leave every choice to you. Each level shows what it would actually find — *"312
items · 1.4 GB"* — so the choice is made on consequences rather than on adjectives.

### Why two hashes

A gradient hash finds candidates; a DCT hash confirms them. They fail differently, and that
turns out to be load-bearing rather than belt-and-braces. Measured over 100 photographs
against manufactured shares and manufactured near-misses:

| | gradient hash | coarse hash |
|---|---|---|
| re-encoded copies | up to 10 bits | up to 6 bits |
| tighter crop, small pan | down to 6 bits | down to 8 bits |

The gradient hash **overlaps** — no threshold on it alone separates a WhatsApp copy from a
differently-framed photo. The coarse hash leaves a clean gap at 7, which is where the wall
sits. At those settings every manufactured share was found and no crop was admitted.

Two further guards: a **detail floor**, because solid colours, blank pages and dark frames
hash identically to each other and would otherwise all merge — they are excluded rather than
matched; and an **aspect-ratio check**, since rescaling preserves shape.

Crops are out of scope. A global hash sees a crop as a different picture, and catching those
needs block-level matching.

### Videos

Levels 1 and 2 only — filters and bursts mean nothing for a clip. Frames are sampled across
the running time and compared frame by frame, with a majority required to agree.

Duration is what makes this affordable: a re-encode keeps its length, so a clip that shares a
running time with nothing else cannot have a twin and is never decoded at all. Only the few
that collide pay for frame extraction.

### Which copy is kept

A favourite, then the copy that was not received from somewhere (`WhatsApp Images/`,
`Bluetooth/`, `Download/`, or a `-WA` file name), then the most pixels, then the most bytes,
then the oldest. It is a suggestion — every tile toggles, and nothing leaves the gallery
until you empty the bin.

## Solution layout

```
SortAndDelete.slnx
├── SortAndDelete.csproj        MAUI app (net10.0-android / net10.0-ios)
│   ├── Services/               GalleryService, DuplicateScanner, album picker
│   ├── ViewModels/ Views/      MVVM (CommunityToolkit.Mvvm), Shell navigation
│   └── Platforms/              MediaStore (Android) / Photos (iOS) implementations
├── Core/SortAndDelete.Core     platform-free logic (net10.0)
│   ├── Models/                 PhotoAsset, ReviewRecord, ImageSignature, …
│   ├── Logic/                  month aggregation, deck queues, streaks,
│   │                           perceptual hashing, duplicate grouping
│   └── Data/                   SQLite: ReviewStore (decisions), SignatureStore (fingerprints)
└── Tests/SortAndDelete.Tests   xUnit suite for everything in Core
```

The hashing and grouping live in `Core` and take raw pixels, so they carry no imaging
dependency and the unit tests exercise the same code the phone runs. Platforms supply the
pixels: `Bitmap`/`MediaMetadataRetriever` on Android, `PHImageManager`/`AVAssetImageGenerator`
on iOS.

## Build, test, run

```bash
dotnet test                                # 271 unit tests
dotnet build -f net10.0-android -t:Run     # Android (device/emulator attached)
dotnet build -f net10.0-ios -t:Run         # iOS (needs a Mac)
```

Requirements: .NET 10 SDK with `android`/`ios` workloads (ships with Visual Studio's MAUI
workload). Android 10 (API 29)+ / iOS 15+.

### Platform notes

| | Android | iOS |
|---|---|---|
| Delete → 30-day trash | `MediaStore.createTrashRequest` (API 30+) | `PHAssetChangeRequest.DeleteAssets` → Recently Deleted |
| Restore after emptying bin | **In-app** (`createTrashRequest(…, false)`) | Photos app → Recently Deleted |
| Move to album | In place via `RELATIVE_PATH` where MediaStore allows it (images: DCIM/Pictures; videos: DCIM/Movies, and Pictures from Android 11), one consent dialog. Download/Documents: copy. Any other folder: granted once through the system folder picker, then copy. A copy is written pending (MediaStore) or under a hidden name (folder grant), fsynced, read back and compared (length + SHA-256), and keeps the date, file time and ⭐; only then does the original go to the system trash (API 30+). Android 10 offers in-place moves only. Folders picked with *Browse* follow the same rules and must be on the device's own storage or SD card. The storage root and `Android/` (other apps' folders) are never targets | Photo added to the album collection |
| Videos | MediaStore files table | `PHAssetMediaType.Video` |
| Permissions | `READ_MEDIA_IMAGES`/`VIDEO` (13+), partial access (14+), `ACCESS_MEDIA_LOCATION` so copy-style moves keep the location (never granted with partial access: a photo without one copies whole, otherwise the user is asked) | Read/write, limited access supported |
| Backup | The two databases (decisions and the duplicate cache, keyed by MediaStore ids) are excluded from cloud backup and device transfer (`dataExtractionRules`, `fullBackupContent`); preferences still go | iCloud device backup |

Gotcha worth knowing: `createTrashRequest`/`createWriteRequest` require **typed** media URIs
(`images/media`, `video/media`) — files-table URIs are rejected. The Android service resolves
each id's media kind (including trashed rows via `QUERY_ARG_MATCH_TRASHED`) before building
consent requests. And a query on an item's own uri finds it even in the system trash
(`is_trashed=1`); whether the gallery still has it is a question for the collection.

The in-app bin exists precisely so that *accidental* swipes are always recoverable in one tap,
regardless of platform restrictions.
