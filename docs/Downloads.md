# Downloads

Download inside Koma using Nyaa search, a magnet link or a `.torrent` file. Transfers use [MonoTorrent](https://github.com/alanmcgovern/monotorrent); you don't need a separate torrent app.

## Start a download

1. Open **Downloads → Browse Nyaa**, search a title and choose a release. Search uses the configured [nyaa.media](https://nyaa.media/) site.
2. Press **Download**. You can also use **Add magnet** or **Open torrent**.
3. Watch progress in the queue. Completed episodes appear in your library automatically.

Downloads save to your Anime folder by default. Change the destination and speed limits in **Download settings**. Existing jobs keep their original destination.

## Queue controls

- **Pause / Resume:** stop or continue a transfer. Unfinished downloads reopen paused.
- **Open folder:** find a completed download on disk.
- **Remove:** remove the job while keeping its files. Removed partial jobs aren't automatically restored.

Torrents upload while downloading. Koma stops sharing after completion and stops transfers when closed. Opening the app doesn't resume transfers by itself.

## Missing from the library?

Only completed files appear. Try **Refresh** and check the job's status.

Older completed jobs may have temporary `.!mt` filenames. On startup, Koma verifies queued jobs and restores valid files automatically. If verification fails or a filename conflicts, your files are kept; **Resume** retries the check. Removed jobs no longer have the metadata needed for automatic recovery.

Download details are protected for your Windows account. Completing a download doesn't mark an episode watched or update MyAnimeList. Automatic subscriptions aren't supported yet.

![Download browser with sample results](screenshots/downloads-preview.png)
