# MyAnimeList tracking

Connect MyAnimeList to see your list in Koma and update episode progress while you watch. Tracking is optional; local playback works without it.

## Set it up

1. Open **Tracking → Connect MyAnimeList**.
2. Enter your own registered Client ID if asked, then sign in and approve Koma in your browser. No shared ID or client secret is bundled.
3. Turn on **Automatically identify anime and update MyAnimeList**.

Registering your own app? Use `http://127.0.0.1:43857/callback` as its redirect URL. This address points back to Koma on your PC. [MyAnimeList app registration](https://myanimelist.net/apiconfig/create)

Koma tries to identify the show automatically. If it needs help, use **Change anime match**. [Choosing the right anime](Recognition.md)

## When progress updates

An episode needs **80% actual watched coverage**. Skipping to the ending doesn't count.

Episodes update in order. If MyAnimeList says episode 4 and you watch episode 6, Koma waits for episode 5 before advancing. Rewatches, conflicting progress and shows marked on hold, dropped or completed need review.

The dashboard shows the current status. Use **Sync now** to refresh or retry. Koma updates episode progress and, when needed, Watching/Completed status; it leaves scores, notes and tags alone.

## Privacy and disconnecting

Koma sends anime titles for matching and show IDs/progress for tracking. Your videos, file paths and detailed viewing history stay on your PC.

Sign-in details are protected for your Windows account. **Disconnect** removes the saved account connection and turns automatic updates off. Your local library and progress remain.

**Beta note:** account connection and list loading have been tested. Real progress updates still need live validation. Airing schedules and release reminders aren't available yet.

![Tracking dashboard with sample data](screenshots/tracking-preview.png)
