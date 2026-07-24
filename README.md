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
- **Undo** and a **Later** button for hard choices.

### Progress & resume
- **Months overview** — progress bar, counts and cover thumbnail per month.
- **Continue where you left off** — every decision is stored in SQLite; reviewed photos never
  come back, and the last month you worked on is offered as a "Continue" card.
- **Streak** — 🔥 daily streak + reviewed-today counter.
- **Stats** — total items, % reviewed, storage freed.

### Smart decks
- **Duplicates** 🧬 — perceptual dHash scan (cached in SQLite, runs once), near-identical photos
  grouped transitively; keep-the-biggest preselected, tap to toggle, one button sends the rest
  to the bin.
- **Screenshots** 📸 — auto-detected (folder on Android, media subtype on iOS).
- **Blurry** 🌫️ — variance-of-Laplacian scoring during the same scan; blurriest first.
- **Biggest files** 🐘 — largest items first, the fastest way to free space.

### Albums
- **Move to albums** — send a photo to an existing folder/album ("Docs", "Travel", …) or create
  a new one. Android physically moves the file; iOS adds it to the album (how Photos works).

## Solution layout

```
SortAndDelete.slnx
├── SortAndDelete.csproj        MAUI app (net10.0-android / net10.0-ios)
│   ├── Services/               GalleryService, album picker
│   ├── ViewModels/ Views/      MVVM (CommunityToolkit.Mvvm), Shell navigation
│   └── Platforms/              MediaStore (Android) / Photos (iOS) implementations
├── Core/SortAndDelete.Core     platform-free logic (net10.0)
│   ├── Models/                 PhotoAsset, ReviewRecord, MonthGroup, …
│   ├── Logic/                  month aggregation, deck queues, streaks
│   └── Data/ReviewStore        SQLite persistence
└── Tests/SortAndDelete.Tests   xUnit suite for everything in Core
```

## Build, test, run

```bash
dotnet test                                # 44 unit tests
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
| Move to album | File moves via `RELATIVE_PATH` update (per-item consent dialog) | Photo added to the album collection |
| Videos | MediaStore files table | `PHAssetMediaType.Video` |
| Permissions | `READ_MEDIA_IMAGES`/`VIDEO` (13+), partial access (14+) | Read/write, limited access supported |

Gotcha worth knowing: `createTrashRequest`/`createWriteRequest` require **typed** media URIs
(`images/media`, `video/media`) — files-table URIs are rejected. The Android service resolves
each id's media kind (including trashed rows via `QUERY_ARG_MATCH_TRASHED`) before building
consent requests.

The in-app bin exists precisely so that *accidental* swipes are always recoverable in one tap,
regardless of platform restrictions.
