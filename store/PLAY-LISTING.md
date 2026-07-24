# Google Play — paste-ready content for Sort & Delete

## Store listing (Grow → Store presence → Main store listing)

**App name** (30 chars max)
```
Sort & Delete: Gallery Cleaner
```

**Short description** (80 chars max)
```
Sort your gallery month by month. Keep, delete, organize — fully offline.
```

**Full description** (4000 chars max)
```
Clean your photo gallery the easy way. Sort & Delete turns tidying thousands of photos into a quick decision game: one tap to keep, one tap to delete — one month at a time.

DELETE WITHOUT FEAR
• Deleting never removes anything immediately: photos go to the in-app bin first.
• Restore any photo from the bin with one tap, or undo your last decision instantly.
• Emptying the bin moves photos to your device's system trash, where Android keeps them for about 30 days — and you can restore them from inside the app during that time.

MONTH BY MONTH, AT YOUR PACE
• Your gallery is organized into months with progress bars.
• Stop anytime — the app remembers every decision and continues exactly where you left off.
• Re-review any month (or several at once) whenever you like.
• Keep your streak going with the daily counter.

ORGANIZE AS YOU GO
• Move photos to folders like "Docs" or "Travel", create new ones, or browse to any folder on your device — even WhatsApp photos move correctly.
• Filter by folder or by your ⭐ favourites.
• "Biggest files first" mode frees storage fastest.
• Videos are supported, with duration and file size shown.

FULL QUALITY, FULL DETAIL
• Photos render at gallery-level quality with double-tap zoom.
• See each photo's date, file name, location and size on the card.
• Open any photo or video in your favorite gallery app with one tap.

100% PRIVATE
• No internet access — the app cannot send anything anywhere.
• No accounts, no ads, no analytics, no tracking.
• Everything stays on your device.
```

**App icon**: upload `store/icon-512.png` (512×512)
**Feature graphic**: upload `store/feature-graphic-1024x500.png`
**Phone screenshots**: upload the 4 files in `store/screenshots/`

Category: **Tools** (or Photography) · Tags: photo manager, gallery cleaner
Contact email: m.ismail@mail.ru

---

## Policy → App content — form answers

**Privacy policy URL**: your hosted copy of `docs/privacy-policy.html`
(e.g. `https://ismayil-musayev.github.io/SortAndDelete/privacy-policy.html` once the repo
is pushed to GitHub as `SortAndDelete` with Pages serving the `docs/` folder)

**App access**: All functionality is available without special access (no login).

**Ads**: No, the app does not contain ads.

**Content rating questionnaire**: category *Utility*; answer **No** to everything
(no violence, sexuality, language, controlled substances, user interaction,
sharing location, personal info, digital purchases). Result: rated for everyone / PEGI 3.

**Target audience**: 18 and over (simplest — avoids child-policy requirements).
"Appeals to children": No.

**News app**: No.

**Data safety**:
- Does your app collect or share any of the required user data types? → **No**
- Result shown to users: "No data collected · No data shared"

**Government app**: No. **Financial features**: none. **Health**: none.

**Photo and Video Permissions declaration** (appears after you upload the .aab,
because of READ_MEDIA_IMAGES / READ_MEDIA_VIDEO):
- Select: the app needs broad access because photo/video management is its
  **core functionality**.
- Core use case: **Photo/video gallery management** — the app lets users browse
  their entire photo library to review, organize, move and delete photos and
  videos. A photo picker cannot serve this purpose because the app must show
  and manage the complete library, including counts and month-by-month progress.

---

## Upload

File to upload: `artifacts/SortAndDelete-v1.0.aab`
Track for the first upload: **Internal testing** (instant, installable via link),
then promote to Production when the forms are done.

When Play asks about **Play App Signing** on first upload: accept/continue
(Google manages the app signing key; your keystore in `signing/` is the upload key).
