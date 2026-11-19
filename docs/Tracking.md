# MyAnimeList tracking and dashboard — local preview

The current source implements optional MyAnimeList account sign-in, a tracking dashboard and automatic episode updates. These features are **not included in the public beta 5 download**. Live catalogue matching and account connection/list loading have been verified; writing progress to a real account has **not** been verified. This is a development preview, not a release-readiness claim.

Local playback and the library remain usable without a network connection or MyAnimeList account. [Anime recognition](Recognition.md) can identify local filenames and search the catalogue independently of account tracking.

## The dashboard

![Tracking dashboard with a synthetic account and local test episodes](screenshots/tracking-preview.png)

This development screenshot uses canned account responses and synthetic video files, not a real user's list.

The **Tracking** page combines local shows with the connected account's loaded MyAnimeList entries. Rows show local episode availability, episodes actually watched in Koma, remote episode counts and any review or synchronization message. **Open** returns to the local show's episodes; remote-only entries cannot start playback.

Search by title or filter **All anime**, **On this PC**, **Watching**, **Completed**, **Plan to watch** or **Needs review**. The counters distinguish local shows, list entries loaded and rows requiring review. Account lists load in pages, with **Load more** for additional entries; the loaded count is not presented as the account's total. **Sync now** refreshes account data and retries eligible updates when automatic tracking is enabled. Account errors leave local playback available.

## Connecting an account

This preview requires the app owner's privately configured, registered MyAnimeList Client ID. The identifier is not bundled in source or packages, and no client secret is requested. If no ID is saved, **Connect MyAnimeList** offers a masked entry field before sign-in.

Connecting opens the system browser. The user must sign in and approve Koma themselves. Koma temporarily listens only on `127.0.0.1` at the registered `http://127.0.0.1:43857/callback` address; this is a callback on the same PC, not a public website. A five-minute deadline, random one-use state and PKCE tie the reply to the current attempt. Cancellation or a busy callback port leaves the account disconnected. The implementation uses the `plain` PKCE method shown in [MAL-Sync's OAuth implementation](https://github.com/MALSync/MALSync/blob/master/src/utils/oauth.ts), alongside the [MyAnimeList authorization reference](https://myanimelist.net/apiconfig/references/authorization). It does not reuse MAL-Sync's identifier or credentials.

The Client ID is sent in the standard OAuth authorization URL during account sign-in. Catalogue searches instead send it in the `X-MAL-CLIENT-ID` header. Do not publish full authorization URLs, credentials or token files.

Access and refresh tokens are stored separately from normal preferences using Windows current-user protection. Token expiry drives refresh, and replacement tokens are saved atomically. The protected files are tied to the Windows profile; moving a portable folder to another PC or account does not transfer a usable connection. [Windows data protection](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)

## Enabling episode updates

Account connection alone does not enable writes. **Automatically identify anime and update MyAnimeList** is off by default and is reset after a new connection. Once enabled, Koma first matches against the user's loaded list and aliases, then searches the catalogue when needed for the selected/playing title.

Clear, unique, exact regular-episode matches can link and approve normal zero-offset numbering automatically. Unknown numbering, incomplete results, remakes and ambiguous seasons ask for review. The visual chooser shows English/original titles, covers, years, type and episode count. **Track this show** saves both the selected entry and episode-number offset in one action. The player shows the active match and watched-percentage threshold. See [automatic matching and the chooser](Recognition.md).

Numbering permission remains scoped to the connected account and exact remembered match. A manual correction wins over late results, and disabling a show prevents automatic re-enabling. Range/special/unknown-number files cannot be guessed into normal episode updates.

Koma counts **unique watched playback**, separately from resume positions and the player's end-of-file marker. A file becomes eligible after at least **80%** of its media duration has actually been covered. Replaying the same section does not increase coverage twice. Seeking, pauses, unexpected position jumps and long gaps in playback observations do not supply viewing evidence. Coverage survives a normal resume, while changed file length or modification time invalidates previous evidence. Automatic updates require media at least **60 seconds** long.

MyAnimeList stores a series-level episode count. If remote progress is 4 and episode 6 was watched locally, Koma keeps the count at 4 until episode 5 also qualifies. Episodes 5 and 6 can then advance it to 6. Updates never automatically lower or reset remote progress. Rewatches, dropped or on-hold entries, remote deletion or a detected count reduction stop for review. The app re-reads the entry immediately before a write and refuses changed or completed entries. There is no server-side atomic compare-and-set, so a simultaneous edit from another client remains a limitation.

Updates send the anime ID, watched episode count and, when needed, the corresponding **Watching** or **Completed** status. Scores, tags, notes and dates are not submitted. An ambiguous write failure is not blindly replayed: the dashboard asks for review and **Sync now** reads the remote state before another attempt. [MyAnimeList API reference](https://myanimelist.net/apiconfig/references/api/v2)

## Local data and disconnecting

Provider profile and list payloads stay in memory and are cleared on disconnect. Local preferences retain the user's match choices, account-specific numbering approvals, watched interval evidence and numeric synchronization receipts used to detect remote reductions. OAuth tokens and the Client ID use separate Windows-protected files rather than settings JSON.

Catalogue matching sends the chosen anime title. Account synchronization sends the provider IDs and consented progress fields. Video data, full local paths, filenames, resume positions and detailed viewing intervals are not uploaded. Requests use fixed official endpoints; server-supplied paging URLs are not followed. **Disconnect** cancels account operations, removes the saved account tokens, clears the loaded list and turns automatic updates off. Local library/progress and the separately configured catalogue connection remain available.

Registration of the owner's app does not establish permission to distribute shared credentials. The public Client ID distribution model and applicable provider requirements still need review before publishing this feature. No provider approval of this implementation or its terms compliance is claimed. [MyAnimeList API agreement](https://myanimelist.net/static/apiagreement.html)

## Verification and remaining work

The Release build and all **27 console checks** pass. Coverage includes watched-interval accounting, seeking/replay/pause behavior, account-bound numbering approvals and settings persistence, contiguous episode progress, fixed-host account requests, pagination, field-preserving updates and remote conflicts. Isolated synthetic OAuth callback and Windows-protected token-storage checks also pass. They use dummy data and canned HTTP responses, not a real account.

The actual WPF dashboard passed offline defaults, synthetic account/list connection, numbering-dialog validation, out-of-order episodes, opt-out, one coalesced progress update, duplicate prevention, remote-reset warnings, search, direct-file review context, keyboard isolation, disconnect cleanup and English/German/French layouts down to 680 × 480. Real mpv playback with synthetic media confirmed that seeking to EOF does not complete watched coverage and sustained playback at 2× reaches the 80% threshold. The recognition review interface also passed its regression checks with dummy credentials.

Before release, verify the actual browser login and callback, live account list loading/pagination, token refresh, account switching, an explicitly enabled real progress update and offline recovery. Physical display scaling remains unverified. Existing live catalogue validation establishes automatic and manually reviewed recognition for the tested series; it does not validate account tracking.

## Future episode timing

Airing schedules and release notifications are not implemented. A future **Airs at** value must identify its provider and time zone; passing a broadcast time is not proof an episode is available to stream. **On this PC** must continue to come from the local folder scan. Regional availability needs evidence from the relevant licensed service. Schedule-provider permissions and data-retention requirements need separate review before that feature is enabled.
