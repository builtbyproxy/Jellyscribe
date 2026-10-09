export type ReleaseCategory = 'new' | 'improvements' | 'fixes' | 'breaking';

export type ReleaseNotes = {
  version: string;
  headline: string;
  summary?: string;
  highlights: Partial<Record<ReleaseCategory, string[]>>;
  upgradeNotes?: string;
};

export const releaseNotes: ReleaseNotes[] = [
  {
    version: '2.12.3',
    headline: 'Editing an account keeps every setting it already has',
    summary:
      'When an admin edits a linked diary on the Jellyscribe settings page, the save now starts from the account as it is stored and changes only what the form shows. Before, the page rebuilt the account from scratch, so any account setting the form did not list would have been reset to its default on the next edit. Nothing is lost today, because every current setting is on the form, but future settings are now safe from the moment they ship.',
    highlights: {
      fixes: [
        'Editing an account on the admin settings page keeps every stored setting the form does not show, instead of resetting it.',
      ],
    },
  },
  {
    version: '2.12.2',
    headline: 'Smaller settings pages that update themselves after an upgrade',
    summary:
      'Both Jellyscribe dashboards, the admin settings page and your own page, now share one script and one stylesheet instead of each carrying its own copy. The pages are about five times smaller, and the shared part, fonts included, is downloaded once per version and then cached. They look and behave exactly as before. After a plugin upgrade, an open browser tab picks up the new version the next time the page is opened, with no reload needed. When you post a review that lands on a diary entry you had already logged, the result now says so, and any note from the server is shown under that account. The design follows Wouter Stulp\'s shared-assets work in his fork, reworked to keep the bundled fonts and to load the right version after an upgrade.',
    highlights: {
      improvements: [
        'Both dashboards share one cached script and stylesheet, so each page is about five times smaller and looks exactly the same, with the bundled fonts kept (design by Wouter Stulp)',
        'An open tab loads the new dashboard code after a plugin upgrade without a reload',
        'The review result says when a review was added to the diary entry you already had, and shows any note from the server',
      ],
    },
  },
  {
    version: '2.12.1',
    headline: 'A review of a film already in your diary goes onto that entry',
    summary:
      'Reviewing a film from your activity list no longer adds a second watch to your Letterboxd diary. When the film is already in your diary from a sync, the review and its star rating now go onto that diary entry, keeping its original date. A review is never written over one you already wrote on Letterboxd: you are asked to edit that one on Letterboxd instead. If the entry can\'t be found, or your account is signed in through the Letterboxd website rather than the official API, the review is saved as a new entry on the day you watched the film rather than today, and the result tells you so.',
    highlights: {
      fixes: [
        'Reviewing a film already in your diary puts the review and rating on that diary entry instead of logging a second watch dated today.',
        'A review that can\'t be added to the existing entry is saved on the day you watched the film, not today, and the result says why.',
        'A review you already wrote on Letterboxd is never overwritten.',
      ],
    },
  },
  {
    version: '2.12.0',
    headline: 'Search and your whole history in the activity list',
    summary:
      'The activity list now reaches your whole history. A search box above the status filters finds any title. A "Load older history" button pages back through everything Jellyscribe has logged, instead of the list quietly stopping after a couple of hundred entries. A binge of one show folds into a single row that shows the episode range and any failures, and opens with a click or the keyboard. Prev and Next switch off at the ends. The same works on the admin dashboard\'s overview and its Activity tab. In the account dialog, the watchlist option\'s description now changes when you switch a new account between Serializd and Letterboxd, and the service names read "Serializd: TV" and "Letterboxd: Film". Thanks to Wouter Stulp, whose search, episode grouping and older-history work is included.',
    highlights: {
      new: [
        'Search your activity by title on both dashboards',
        'Load older history pages back through everything Jellyscribe has logged, with a count of what is loaded',
        'A binge of one show folds into a single row you can open, showing the episode range and any failures (thanks to Wouter Stulp)',
      ],
      improvements: [
        'Prev and Next switch off at the ends of the activity list and the admin Activity tab',
        'An empty search says when older history is still left to load',
      ],
      fixes: [
        'The activity list no longer stops silently after a couple of hundred entries',
        'An entry that shifted between two pages no longer shows twice',
        'Switching a new account to Letterboxd now rewords the watchlist option, which kept its TV wording',
      ],
    },
  },
  {
    version: '2.11.1',
    headline: 'Episode reviews on single-season shows, and gentler rate-limit handling',
    summary:
      'Jellyscribe is steadier when Letterboxd or Serializd ask it to slow down. A rate-limit pause is honoured for up to a minute. Anything longer skips the film or episode until the next sync, and a Letterboxd sync that keeps hitting the limit stops early instead of trying every film. Letterboxd retries are now accepted instead of refused as a repeat. Stopping a sync, or restarting Jellyfin, no longer waits out a long pause. When you sign in with your Letterboxd website login, a TV series that Letterboxd lists as a film can no longer be mistaken for a movie with the same TMDb number, so the wrong film is never marked watched or rated. A Serializd review of an episode now lands on the same episode a log would: for a show Serializd lists as a single season, it goes to the right episode of that season, and it is refused if Serializd has no such episode. The activity lists on both dashboards show the full page they ask for again.',
    highlights: {
      improvements: [
        'A rate-limit pause from Letterboxd or Serializd is honoured for up to a minute. A longer one skips the item until the next sync, and a Letterboxd sync that keeps hitting the limit stops early.',
        'Stopping a sync or restarting Jellyfin no longer waits out a rate-limit pause or a retry backoff.',
      ],
      fixes: [
        'Letterboxd retries after a rate limit are now accepted instead of refused as a repeat.',
        'With the Letterboxd website login, a TV series that Letterboxd lists as a film is no longer mistaken for a movie with the same TMDb number.',
        'A Serializd episode review on a show Serializd lists as a single season now lands on the right episode, and is refused if Serializd has no such episode.',
        'The activity lists on both dashboards show the full page they ask for again.',
      ],
    },
  },
  {
    version: '2.11.0',
    headline: 'See exactly what telemetry and log bundles send',
    summary:
      'You can now see exactly what Jellyscribe would send before anything leaves your server. In the admin settings, Integrations has a Preview of the anonymous telemetry ping, the exact JSON, with Copy and "Copy + regenerate ID" for bug reports, and a Regenerate ID button that gives your server a fresh anonymous identity whenever you like. The admin Overview asks once whether you would like to turn telemetry on, and never again after you answer. "Send to developer" on the Logs tab now has a Preview of the full bundle, your note included, and its confirmation lists everything the bundle holds. Email addresses no longer appear in Jellyscribe\'s logs or in a bundle: Serializd accounts are named by a short tag, and any address in older log lines is masked. A successful review\'s reply is no longer written to the log, and replies that older versions logged are cut from what you can send. The telemetry description is also more precise: usage counts are rough buckets, error counts are exact per week, and an extra ping goes out at most once a day when a new kind of error starts.',
    highlights: {
      new: [
        'Preview the exact anonymous telemetry ping from Integrations, with Copy and Copy + regenerate ID for bug reports.',
        'Regenerate ID gives your server a fresh anonymous telemetry identity whenever you like.',
        'Send to developer has a Preview of the full log bundle, your note included, before anything is uploaded.',
        'A one-time question on the admin Overview about turning on anonymous telemetry, which never returns once answered.',
      ],
      improvements: [
        'The Send to developer confirmation lists everything a bundle holds, including that log lines name films, shows and usernames.',
        'The telemetry description says exactly what is sent: bucketed usage counts, exact weekly error counts, and an extra ping at most once a day when a new kind of error starts.',
      ],
      fixes: [
        'Email addresses no longer appear in Jellyscribe\'s logs or in a log bundle; Serializd accounts are named by a short tag.',
        'A successful review\'s reply, which can repeat the review, is no longer written to the log, and replies older versions logged are cut from what you can send.',
        'A log bundle preview now matches what is sent byte for byte, even when telemetry has never been on.',
      ],
    },
  },
  {
    version: '2.10.10',
    headline: 'Settings pages that work by keyboard, on phones and in both themes',
    summary:
      'The Jellyscribe pages now work properly on a phone, by keyboard and with a screen reader, and they follow your Jellyfin theme. The admin settings page no longer runs off the side of a phone screen, and on a phone each activity row shows its date under the title, an idea from Wouter Stulp. The section menu, the star rating and the account and review windows can all be used with the keyboard. The windows now behave as proper dialogs: they keep focus inside, close with Escape and return you to where you were. Every field is labelled for screen readers. Thanks to Wouter Stulp, buttons and small text are much easier to read, disabled buttons look disabled, and the pages no longer restyle the rest of Jellyfin. The pages now switch between light and dark with your Jellyfin theme instead of your device\'s setting, and in light mode every status colour is readable. "Back to Jellyfin" now works on servers that live under a sub-path such as /jellyfin, and returns you to the page you came from without reloading.',
    highlights: {
      improvements: [
        'The pages follow your Jellyfin theme, light or dark, instead of your device\'s setting.',
        'The section menu, the star rating and the account and review windows all work by keyboard, and every field is labelled for screen readers.',
        'Clearer buttons and small text, with disabled buttons that look disabled, thanks to Wouter Stulp.',
        'Every status colour is readable in light mode.',
        'On a phone, each activity row shows its date under the title, an idea from Wouter Stulp.',
      ],
      fixes: [
        'The admin settings page no longer runs off the side of a phone screen.',
        'The Jellyscribe pages no longer restyle the rest of Jellyfin\'s interface, a fix from Wouter Stulp.',
        '"Back to Jellyfin" works on servers under a sub-path such as /jellyfin and returns you to the page you came from without a reload.',
        'Leaving the admin page while a sync runs no longer keeps checking its progress in the background.',
      ],
    },
  },
  {
    version: '2.10.9',
    headline: 'The settings pages never lose or duplicate an account',
    summary:
      'The Jellyscribe dashboards now keep your accounts safe when something goes wrong. If your accounts or activity can\'t be loaded, the page says so and offers Retry instead of showing an empty list or zeros, so a save can no longer wipe your linked diaries. A failed or repeated Save never adds an account twice, and Remove now asks you to confirm in the page and only removes the account once the server has. Every save, sync, login check and review now tells you when it fails and why, rather than hanging on "Saving…" or claiming everything was up to date, and a sync whose progress can\'t be read stops with a clear message. A review with only a star rating now sets your Letterboxd rating instead of always failing. A review that lands on some of your accounts says which ones it missed. Review is no longer offered on rows that can\'t be reviewed. The rewatch date defaults to your own local date, and selecting text in a modal no longer closes it. On the admin dashboard, saving settings no longer undoes account changes your users made in the meantime, Sync all now also runs the TV sync for every user, and the overview is labelled as your own activity. Opening Jellyscribe from the sidebar while the admin settings page is still loaded now works. Thanks to Wouter Stulp, whose fixes for showing load and save failures and for reporting the Integrations save result are included.',
    highlights: {
      improvements: [
        'Remove asks you to confirm in the page, and only removes the account once the server has (both dashboards).',
        'A review with only a star rating sets your Letterboxd rating, and a review that reaches some of your accounts names the ones it missed.',
        'On the admin dashboard, Sync all now also runs the TV sync for every user, and the overview is labelled as your own activity.',
      ],
      fixes: [
        'A failed account or activity load shows an error with Retry instead of an empty list or zeros, so a save can no longer overwrite your accounts (thanks to Wouter Stulp).',
        'A failed or repeated Save never adds an account twice, and every save shows the server\'s reason when it fails (thanks to Wouter Stulp).',
        'The Integrations Save reports Saved or the reason it failed (thanks to Wouter Stulp).',
        'Admin saves no longer undo account changes users made while the dashboard was open.',
        'Sync now reports why a sync could not start, and stops with a clear message when its progress can\'t be read.',
        'Login checks and reviews no longer hang on Verifying or Posting when the server can\'t be reached, and Post review can\'t be pressed twice.',
        'Review is no longer offered on rows that can\'t be reviewed.',
        'The rewatch date defaults to your local date.',
        'Selecting text in a dialog no longer closes it.',
        'Opening Jellyscribe from the sidebar works while the admin settings page is still loaded.',
      ],
    },
  },
  {
    version: '2.10.8',
    headline: 'Faster, steadier syncs on big libraries',
    summary:
      'Syncs get faster and calmer on big libraries, building on work by Wouter Stulp, whose changes to scheduling, caching, connection reuse and history upkeep are included here. The scheduled tasks now run at fixed, staggered times overnight (Letterboxd from 3:00, Serializd from 4:00, telemetry at 5:00) instead of a day after each restart, so if you kept the default schedule your syncs move to these times, and a server that is off overnight still syncs at least every two days. Sync history no longer grows forever: old skipped and failed entries are trimmed without forgetting anything the sync relies on, and history files are rewritten safely so a crash can no longer truncate them. Films and shows are looked up once instead of on every run, and the review window and watchlists no longer scan the whole library. On accounts that use the Letterboxd website, real-time and rating sync reuse the signed-in session instead of logging in again for every film. When Cloudflare blocks the website, a run pauses that account after a few films instead of spending a minute on each one; a struggling Serializd is given room, a catch-up stops after a few failures in a row, and Serializd writes are never retried in a way that could log an episode twice. An account paused by repeated login failures now tries one login a day for a week, so a Letterboxd outage clears on its own, while a rejected password still waits for you to re-save it. Films without a TMDb id are noted once instead of on every run, the diary imports wait for a running sync instead of competing with it, and Jellyfin no longer waits on large history files at startup.',
    highlights: {
      improvements: [
        'Scheduled tasks run at fixed, staggered times overnight; if you kept the default schedule, your syncs move to these times, with a two-day fallback for servers that are off at night (thanks to Wouter Stulp)',
        'Sync history stays small: old skipped and failed entries are trimmed without forgetting anything the sync relies on (thanks to Wouter Stulp)',
        'Films, seasons and library items are looked up once instead of on every run (thanks to Wouter Stulp)',
        'Connections to Letterboxd, Serializd and Seerr are reused across syncs (thanks to Wouter Stulp)',
        'Real-time and rating sync on the Letterboxd website reuse the signed-in session instead of logging in each time',
        'Jellyfin starts faster with a large sync history',
        'Films without a TMDb id are noted once instead of on every run',
      ],
      fixes: [
        'History and cache files are rewritten safely, so a crash can no longer truncate them (thanks to Wouter Stulp)',
        'A Cloudflare block pauses that account\'s run after a few films instead of costing a minute per film',
        'A Serializd catch-up stops after repeated failures and never retries a write that might already have been saved',
        'An account paused by login failures tries one login a day for a week, so an outage clears on its own',
        'The Letterboxd and Serializd diary imports wait for a running sync instead of competing with it',
      ],
    },
  },
  {
    version: '2.10.7',
    headline: 'Every account synced on the right day, once',
    summary:
      'Jellyscribe now keeps track of each linked Letterboxd account separately. If two people share one Jellyfin login, the second account gets its own diary entries instead of being skipped because the first account already logged the film. Watches are now logged on the day you watched them in your server\'s time zone, whether real-time sync or the scheduled sync catches them. An evening film no longer lands on the next day, and the two no longer disagree. Films already on your diary for that day are remembered, so later syncs stop checking them again. When Letterboxd has a bad few days, those films are no longer given up on: Jellyscribe only stops trying a film when Letterboxd keeps saying it does not have it, or when it has failed on its own for over a week. If Letterboxd cannot answer the duplicate check, the film is retried later instead of risking a second diary entry. Two quick finishes of the same film now log once. A watchlist that cannot be read in full, on Letterboxd or Serializd, no longer removes anything from your playlist, collection or Seerr watchlist. The scheduled sync now marks rewatches. Ratings the plugin imports no longer get pushed back out to your other accounts. Half-point ratings now round the same way on Letterboxd and Serializd (a 4.5 in Jellyfin is 2.5 stars). Diary dates are sent correctly on servers set to a non-Gregorian calendar. Stopping the server mid-sync no longer marks a film as failed.',
    highlights: {
      improvements: [
        'Half-point ratings round the same way on Letterboxd and Serializd',
      ],
      fixes: [
        'A second Letterboxd account on the same Jellyfin user now gets its own diary entries',
        'Watches are logged on the day you watched them in your server\'s time zone, by both real-time and scheduled sync',
        'Films already on your diary are remembered and no longer re-checked on every sync',
        'A few bad days on Letterboxd no longer make Jellyscribe give up on films',
        'A failed duplicate check is retried later instead of risking a duplicate diary entry',
        'Finishing the same film twice in quick succession logs it once',
        'A watchlist that cannot be read in full no longer removes anything from your playlist, collection or Seerr watchlist',
        'The scheduled sync now marks rewatches',
        'Ratings imported by the plugin are no longer pushed to your other Letterboxd accounts',
        'Diary dates are sent correctly on servers using a non-Gregorian calendar',
        'Stopping the server during a sync no longer records a failure',
      ],
    },
  },
  {
    version: '2.10.6',
    headline: 'Saved passwords and keys stay on the server',
    summary:
      'Jellyscribe no longer sends saved passwords, cookies or the Seerr API key back to the browser. Before, the settings pages loaded each linked account\'s Letterboxd or Serializd password and cookies into the form, and the admin settings returned every user\'s password and the Seerr key. Anyone holding a session could read them. Saved values now show as "Saved" with an empty field. Leave it blank to keep the saved value, type to replace it, or tick "Remove saved cookies" or "Remove saved key" to drop it. Verify login works with the saved password, and an admin can move an account to another Jellyfin user or rename it without re-entering its password. Existing saved logins keep working after the upgrade. Thanks to Wouter Stulp, whose fork made stored secrets write-only first; this release builds on his work.',
    highlights: {
      improvements: [
        'Saved passwords, cookies and the Seerr API key show as "Saved" with an empty field: leave it blank to keep it, type to replace it, or tick the remove box to drop it.',
        'Verify login checks the saved password when the field is left blank.',
        'Renaming an account, or an admin moving it to another Jellyfin user, keeps its saved password and cookies.',
      ],
      fixes: [
        'Settings pages and the admin configuration no longer send saved Letterboxd and Serializd passwords, cookies or the Seerr API key back to the browser (thanks to Wouter Stulp).',
      ],
    },
  },
  {
    version: '2.10.5',
    headline: 'Separate Serializd watchlist collections, and Seerr requests made as each person',
    summary:
      'Serializd watchlist collections are now kept separately for each person. Before, everyone who turned on Serializd watchlist sync shared one collection called "Serializd Watchlist", so each person\'s sync removed the other\'s shows. A custom name could also point the sync at any collection on the server, including one an admin curates. Each linked account now gets its own collection, named "Serializd Watchlist (your Jellyfin username)" by default, and Jellyscribe only ever changes the collection it made for that account. On upgrade, if only one account was syncing into the old shared collection, it carries on using it. Otherwise the old collection is left exactly as it is and each account gets a fresh one, so you can delete the old one once you no longer need it. Collections are visible to everyone who can see the shows in them. Custom names for watchlist collections and playlists can now be set only by an admin, from the plugin\'s settings page. Seerr requests are now made as each person\'s own Seerr account, so Seerr applies their own permissions. Admins, note one change: if you have turned off "Auto-approve requests created by Jellyscribe", requests from people without Auto-Approve in Seerr now wait in Seerr\'s approval queue, where before they were approved as the admin. With the switch on, which is the default, requests still go straight through. Thanks to Wouter Stulp, who spotted the Seerr approval problem in his fork.',
    highlights: {
      improvements: [
        'Each linked Serializd account now gets its own watchlist collection, named after its Jellyfin user by default.',
        'Only admins can now set custom names for watchlist collections and playlists, from the plugin\'s settings page.',
      ],
      fixes: [
        'Two people with Serializd watchlist sync no longer share one collection and remove each other\'s shows.',
        'A custom watchlist name can no longer make the sync rewrite a collection someone else curates.',
        'Seerr requests are made as each person\'s own Seerr account, so Seerr applies their own approval rights and quota (spotted by Wouter Stulp).',
        'Admins: with "Auto-approve requests created by Jellyscribe" turned off, requests from people without Auto-Approve in Seerr now wait for approval instead of being approved as the admin.',
      ],
    },
  },
  {
    version: '2.10.4',
    headline: 'Tighter security for servers with several Jellyfin users',
    summary:
      'Jellyscribe is now safer on servers shared by several people. Only administrators can run the Seerr connection test, and a failed test no longer reports details about what lives at the address entered. The sync progress shown on the dashboard no longer names whose sync is running. The activity and stats views refuse to answer when the server cannot tell who is asking, where before they returned everyone\'s history. Login checks for Letterboxd and Serializd are now rate-limited per user and across the server, so nobody can use the server to test passwords and get its address blocked; the dashboard says how long to wait. "Sync watchlist now" for Serializd can no longer start several overlapping runs. Two old account endpoints that nothing used any more were removed, one of which returned a stored password. The Seerr URL field now shows a generic example address. Thanks to Wouter Stulp, whose fork fixed the Seerr test, progress and history issues first.',
    highlights: {
      improvements: [
        'Login checks for Letterboxd and Serializd are rate-limited per user and server-wide, and the dashboard says how long to wait.',
        'The Seerr URL field shows a generic example address.',
      ],
      fixes: [
        'Only administrators can run the Seerr connection test, and a failure no longer echoes details about the address tried (thanks to Wouter Stulp).',
        'Sync progress no longer shows other people\'s Jellyfin or Letterboxd names (thanks to Wouter Stulp).',
        'Activity and stats refuse to answer when the caller cannot be identified, instead of returning everyone\'s history (thanks to Wouter Stulp).',
        'Serializd "Sync watchlist now" runs one at a time and says when a run is already going.',
        'Removed two unused account endpoints, one of which returned the stored password.',
      ],
    },
  },
  {
    version: '2.10.3',
    headline: 'Security fixes for servers with more than one Jellyfin user',
    summary:
      'This release fixes two security problems that affect any server where more than one person uses Jellyscribe, so please update. First, the admin dashboard showed account names, film titles and error messages as raw page content, so a user could save a specially crafted account name that ran code in an admin\'s browser the next time they opened the dashboard. Every value is now shown as plain text, and an old, unused stats page with the same problem has been removed. Second, Jellyscribe remembered Letterboxd and Serializd logins by account name alone, so a user who entered someone else\'s Letterboxd username or Serializd email with any password could end up syncing, reviewing and rating as that person. A remembered login is now only reused when the password matches too. Households that deliberately share one Letterboxd or Serializd account across several Jellyfin users are unaffected. Both problems were found and fixed by Wouter Stulp. Thank you, Wouter.',
    highlights: {
      fixes: [
        'The admin and user dashboards show every account name, title and error as plain text, so saved values can no longer run code in an admin\'s browser.',
        'A remembered Letterboxd or Serializd login is only reused when the password matches, so one user can no longer act as another user\'s account.',
        'Removed an old, unused stats page.',
      ],
    },
  },
  {
    version: '2.10.2',
    headline: 'Later anime seasons log to Serializd instead of being skipped',
    summary:
      'Many anime are split into several seasons in Jellyfin but listed on Serializd as one long season. The Apothecary Diaries is a good example: Jellyfin shows a season 1 and a season 2, while Serializd, which follows TMDb, has one long season covering both. Watching season 2 in Jellyfin produced nothing on Serializd, because Jellyscribe asked for a season 2 that Serializd does not have and skipped the episode. Jellyscribe now recognises that shape and logs the episode to season 1 at its running number, so season 2 episode 1 lands on Serializd as episode 25. It only does this for shows Serializd keeps as a single season, when it knows exactly how many episodes the earlier seasons have, and when the running number still falls inside Serializd\'s season. A show Serializd simply has not added a new season for yet, or a library missing episodes that would shift the numbering past Serializd\'s last episode, is still skipped rather than logged to the wrong episode.',
    highlights: {
      fixes: [
        'Episodes from later seasons of shows Serializd lists as a single season (common for anime) are logged to Serializd instead of being skipped.',
      ],
    },
  },
  {
    version: '2.10.1',
    headline: 'Renaming a Jellyfin user no longer empties their Jellyscribe history',
    summary:
      'If you renamed a Jellyfin user, their Jellyscribe dashboard suddenly showed zero films, zero episodes and no recent activity, as if nothing had ever been synced. Nothing was lost: every entry was still there, but each one was filed under the name the user had when it was written, and the dashboard only looked for the new name. The same mix-up quietly weakened the duplicate checks, so a film synced before the rename could be logged to Letterboxd a second time afterwards. History is now tied to the Jellyfin user itself rather than to their name, and existing entries are linked up automatically the first time this version starts. The one case this cannot repair on its own is a user who was already renamed before updating: their older entries stay filed under the old name.',
    highlights: {
      fixes: [
        'The dashboard, stats and recent activity keep showing a user\'s history after their Jellyfin username changes.',
        'Duplicate protection keeps recognising films and episodes synced before a rename, so they are not logged twice.',
      ],
      improvements: [
        'Existing history is linked to its Jellyfin user automatically on first start, so a rename made after updating is handled too.',
      ],
    },
  },
  {
    version: '2.10.0',
    headline: 'Jellyscribe now opens inside Jellyfin, on Jellyfin 12 too',
    summary:
      'Clicking Jellyscribe used to reload the whole Jellyfin web app and drop you onto a plugin settings page. It now opens as a proper page inside Jellyfin, the way Jellyfin Enhanced\'s Bookmarks does: no reload, Jellyfin\'s header and menu stay put, and the back button takes you straight back to where you were. You can also bookmark it, since it has its own address ending in #/jellyscribe. On Jellyfin 12, where the sidebar was replaced by a menu under your profile picture, Jellyscribe now appears in that menu, so it is reachable there for the first time. Everything on the page works as before, and the old settings-page address still works.',
    highlights: {
      new: [
        'Jellyscribe opens as a page inside Jellyfin with no reload, and has its own bookmarkable address (#/jellyscribe).',
        'On Jellyfin 12, Jellyscribe appears in the menu under your profile picture.',
      ],
    },
  },
  {
    version: '2.9.0',
    headline: 'The Jellyscribe sidebar link no longer needs another plugin',
    summary:
      'The Jellyscribe link in Jellyfin\'s sidebar used to appear only if you had also installed the File Transformation plugin, so many people never saw it and had to dig through the dashboard to find their settings. Jellyscribe now adds the link itself, the same way Jellyfin Enhanced adds its own menu, and nothing on your server\'s disk is changed. If you already use File Transformation, nothing changes for you and you still get exactly one link. The link also now works on servers that run Jellyfin under a sub-path such as /jellyfin, where it used to point at the wrong address. On Jellyfin 12, the web client moved its menu into the profile button, so the link doesn\'t show there yet; open Jellyscribe from Dashboard, Plugins instead.',
    highlights: {
      new: [
        'The Jellyscribe sidebar link appears without the File Transformation plugin.',
      ],
      fixes: [
        'The sidebar link works on servers that run Jellyfin under a base URL such as /jellyfin.',
      ],
    },
  },
  {
    version: '2.8.0',
    headline: 'Check your Letterboxd login before you rely on it',
    summary:
      'Until now the Verify login button only worked for Serializd. For a Letterboxd account it said the login would be checked on the first sync, so a wrong password or a mistyped name went unnoticed until something quietly failed to appear on Letterboxd. Verify login now works for Letterboxd too: it signs in the same way syncing will and tells you whether it got in, and if not, the reason Letterboxd gave. It also catches a common mix-up: Letterboxd no longer lets you sign in with your email address, so Jellyscribe now asks for your Letterboxd username instead and explains why if you enter an email.',
    highlights: {
      new: [
        'Verify login works for Letterboxd accounts, in both the admin dashboard and your own settings page, and shows which sign-in method worked or why it failed.',
      ],
      fixes: [
        'Entering an email address as a Letterboxd account name is caught when you verify or save, with a clear message, instead of failing silently at the first sync.',
      ],
    },
  },
  {
    version: '2.7.0',
    headline: 'Ratings you give after watching now reach Letterboxd',
    summary:
      'Jellyscribe sends your rating along when it logs a film to your Letterboxd diary, and that happens the moment the credits roll. Most people rate afterwards, and until now those ratings never left Jellyfin: the film was already logged, so later syncs skipped it. Jellyscribe now notices when you change a film\'s rating and, about ten seconds after you settle on a score, sets it as your Letterboxd rating for that film. Jellyfin\'s own web app has no star rating, only favourites, so the usual way in is a review written with Jellyfin Enhanced 12.10 or later with its option to copy review stars into Jellyfin switched on; ratings from any other app that saves them to Jellyfin work too. Only real changes are sent: ratings already in Jellyfin when you update are left alone until you change them, and favouriting or rewatching a film leaves Letterboxd alone. If Letterboxd is briefly unreachable, the rating is retried a few times. As on Letterboxd itself, rating a film marks it watched and takes it off your watchlist. Clearing a rating in Jellyfin does not remove it from Letterboxd, and some apps, Infuse among them, keep ratings to themselves and never save them to Jellyfin, so those still cannot be synced. Each Letterboxd account has a new Sync ratings to Letterboxd switch, on by default.',
    highlights: {
      new: [
        'Rating changes in Jellyfin are sent to your Letterboxd film rating within seconds, for films you have already logged and for ones you have not.',
        'Reviews written with Jellyfin Enhanced 12.10 or later reach Letterboxd, once its option to copy review stars into Jellyfin is on. Jellyfin\'s own web app only has favourites, not star ratings.',
        'A Sync ratings to Letterboxd switch on every Letterboxd account, in both the admin dashboard and your own settings page.',
      ],
      improvements: [
        'Each rating sent shows up in the activity list as Rated, with the star value.',
      ],
    },
  },
  {
    version: '2.6.0',
    headline: 'Keep a whole library off Letterboxd or Serializd',
    summary:
      'Until now Jellyscribe sent every film and episode you finished to your linked accounts, whichever library it lived in. If you already track some of that somewhere else, anime on AniList for example, those watches were turning up on Letterboxd or Serializd as well, and the only way to stop it was to switch the whole account off. Each linked account now has an Excluded libraries list in its settings. Tick a library there and nothing from it is sent to that account, whether by the daily catch-up or the moment you finish watching. The setting is per account, so two people sharing a Jellyfin login can make different choices, and a library you leave unticked syncs exactly as before. Exclusion only applies from now on: anything already logged stays where it is, and diary import and watchlist sync still look at every library.',
    highlights: {
      new: [
        'An Excluded libraries list on every Letterboxd and Serializd account, in both the admin dashboard and your own settings page.',
        'Films and episodes from an excluded library are skipped by the daily catch-up and by real-time sync alike, and the server log notes how many were skipped.',
      ],
      improvements: [
        'The library list only shows libraries you can actually see, and only the ones that hold films or TV.',
      ],
    },
  },
  {
    version: '2.5.4',
    headline: 'All of Jellyscribe\'s scheduled tasks now sit under one Jellyscribe heading',
    summary:
      'In Jellyfin\'s Scheduled Tasks page, most of Jellyscribe\'s tasks were still filed under a heading called Letterboxd, left over from before the plugin was renamed, while one sat under Jellyscribe. That even put the Serializd tasks for TV under Letterboxd. Every Jellyscribe task, film and TV alike, now appears together under Jellyscribe. Nothing about how or when the tasks run has changed.',
    highlights: {
      fixes: [
        'Jellyscribe\'s scheduled tasks, including the Serializd ones, are grouped under a single Jellyscribe heading instead of being split across Letterboxd and Jellyscribe.',
      ],
    },
  },
  {
    version: '2.5.3',
    headline: 'Big watchlists and diaries now sync in full, not just the first hundred films',
    summary:
      'If your Letterboxd watchlist or your list of watched films was longer than about a hundred titles, Jellyscribe only ever saw the first hundred. A watchlist of a thousand films turned into a playlist of ninety-nine, and the diary import only marked your first hundred watched films as played. The fix shipped in 2.4.0 for this did not hold: it asked Letterboxd for the next page in a way Letterboxd quietly ignores, so every request came back with the first page again and nothing warned that anything was missing. Jellyscribe now asks for each page exactly the way Letterboxd expects, so the whole list comes through, and if Letterboxd ever stops handing over new pages, the sync stops, says so in the log, and leaves your playlist exactly as it was instead of quietly shrinking it. Run a watchlist sync after updating and your playlist will fill in with everything that was missing.',
    highlights: {
      fixes: [
        'Watchlist sync fetches every film on watchlists longer than a hundred, instead of stopping at the first page.',
        'Diary import marks every watched film as played, not only the first hundred.',
      ],
      improvements: [
        'If a list cannot be read in full, the sync stops with a clear error in the log and changes nothing, rather than quietly working from a short list.',
        'Short lists finish in a single request instead of repeating it dozens of times.',
      ],
    },
  },
  {
    version: '2.5.2',
    headline: 'The Raw cookies box no longer vanishes if you use an ad blocker',
    summary:
      'If you run Ghostery, uBlock Origin with annoyance lists, AdGuard or Brave shields, the Raw cookies field in the account settings could simply not appear. The label was there, the box was not, and nothing showed up in the browser console to explain it. The cause was on our side: the field was named with the word "cookie" in it, and blockers carry sweeping rules that hide anything named that way in order to kill cookie consent banners. Ours got caught in the net. The field has been renamed internally so blockers leave it alone, and it still reads Raw cookies on screen. This mattered more than it sounds, because pasting cookies is exactly what we recommend when Cloudflare or two-factor authentication blocks a normal password login, so the people most in need of that box were the ones least likely to see it.',
    highlights: {
      fixes: [
        'The Raw cookies field now appears for everyone, including users running content blockers that were silently hiding it.',
      ],
      improvements: [
        'A test now fails the build if any settings field is named in a way content blockers are known to hide, so this cannot quietly come back.',
      ],
    },
  },
  {
    version: '2.5.1',
    headline: 'Serializd diary import no longer stops at the first season review',
    summary:
      'Importing your Serializd diary could fail outright with a confusing error about an element of type Number. The cause was that Serializd diaries contain reviews of whole seasons and whole shows, not just individual episodes, and those entries carry no episode number. Jellyscribe tried to read one anyway and the whole import stopped there, so nothing was imported at all. Those entries are not episode watches, so they are now simply skipped and the rest of the diary imports normally. If your Serializd import has been failing, it should work on the next run with no changes needed at your end.',
    highlights: {
      fixes: [
        'Serializd diary import no longer fails when the diary contains season-level or show-level reviews, which carry no episode number.',
        'The same fix covers other places a Serializd response can legitimately leave a number empty, so one unusual entry can no longer stop an entire import.',
      ],
    },
  },
  {
    version: '2.5.0',
    headline: 'Watchlist playlists work on Jellyfin 12',
    summary:
      'Jellyfin 12 changed the way plugins add items to a playlist, and Jellyscribe was still asking the old way. The result was that on Jellyfin 12 your watchlist playlist silently stopped being filled in, while everything else carried on working normally. Jellyscribe now detects which version of that API the server offers and uses the right one, so a single release keeps working on Jellyfin 10.11 and Jellyfin 12 alike. If you are on Jellyfin 12 and your watchlist playlist has been sitting empty or stale, this release fixes it and the next watchlist sync will fill it in. Jellyfin 12 is now explicitly supported.',
    highlights: {
      fixes: [
        'Watchlist-to-playlist sync works again on Jellyfin 12. It had been failing silently since Jellyfin 12.0 changed the playlist API.',
      ],
      new: [
        'Jellyfin 12.x is now explicitly supported from the same release that serves Jellyfin 10.11.9 and newer.',
      ],
      improvements: [
        'Every change is now built and tested against the Jellyfin 12 SDK automatically, so this class of breakage is caught before it ships rather than in a bug report.',
      ],
    },
  },
  {
    version: '2.4.2',
    headline: 'Fixing a bad Letterboxd password actually un-pauses syncing again',
    summary:
      'When a Letterboxd login fails three times in a row, Jellyscribe pauses that account and shows "Login failing, sync paused". Correcting the password was supposed to clear that and resume on the next run. It did if you edited the account from the user page, but not from the admin dashboard, because those two save through different paths and only one of them reset the pause. Admins were left stuck: the password was right, the badge still said failing, and even deleting the account and adding it again changed nothing, because the paused state is remembered per Letterboxd username and a new account simply inherited it. Saving an account from the dashboard now clears the pause whenever its password or cookies changed, so a corrected login is retried on the next sync.',
    highlights: {
      fixes: [
        'Correcting a Letterboxd password or cookies in the admin dashboard now clears "Login failing, sync paused" instead of leaving the account stuck indefinitely.',
        'Re-creating an account that was previously paused no longer inherits the old paused state.',
      ],
      improvements: [
        'Saving unrelated settings still leaves a paused account paused, so the protection against hammering Letterboxd with a known-bad password is unchanged.',
      ],
    },
  },
  {
    version: '2.4.1',
    headline: 'Seerr requests that were already stuck now get unstuck',
    summary:
      'Version 2.4.0 made sure new Seerr requests get approved so they actually reach Radarr and Sonarr. It did nothing for requests that were already sitting in Seerr waiting for approval, which for anyone who had been hit by the problem was most of them. The reason is that Jellyscribe skips any title Seerr already has a request for, so those requests were never looked at again and nothing ever approved them. Upgrading appeared to change nothing at all. Jellyscribe now checks for watchlist titles left waiting for approval and approves those too, so an existing backlog clears itself on the next watchlist sync instead of needing to be worked through by hand.',
    highlights: {
      fixes: [
        'Seerr requests left waiting for approval by an earlier version are now approved on the next watchlist sync, so a backlog that never reached Radarr or Sonarr clears itself.',
      ],
      improvements: [
        'Only requests belonging to the synced user, for titles on the watchlist just synced, are touched. Anything else awaiting your approval in Seerr is left alone.',
        'Turning off "Auto-approve requests created by Jellyscribe" also leaves the existing backlog untouched.',
      ],
    },
  },
  {
    version: '2.4.0',
    headline: 'Your whole watchlist syncs, and Seerr requests actually reach Radarr',
    summary:
      'Two problems that quietly cut the watchlist feature off at the knees. Large watchlists only ever synced their first hundred films: the plugin asked Letterboxd for the next page using the wrong field name, so it always concluded there was nothing more to fetch and stopped. Everything past the first page simply never arrived. Separately, watchlist titles sent to Seerr were created but never downloaded. Seerr only hands a request to Radarr or Sonarr once it is approved, and it decides that from the permissions of the person the request belongs to, not from the admin API key the plugin uses, so requests for anyone without auto-approve sat in the pending queue forever. Jellyscribe now approves the requests it creates, and there is a switch in the Seerr settings to turn that off if you would rather review them yourself.',
    highlights: {
      fixes: [
        'Watchlists longer than one page now sync in full. Previously only the first 100 films were ever imported, no matter how large the watchlist.',
        'Titles auto-requested through Seerr now reach Radarr and Sonarr instead of sitting in the pending queue and never downloading.',
      ],
      new: [
        'A new "Auto-approve requests created by Jellyscribe" option in the Seerr settings, on by default. Switch it off to keep plugin-created requests in Seerr\'s manual approval queue.',
      ],
      improvements: [
        'When a request cannot be approved, the log now says so plainly, and names the reason it will not reach Radarr, rather than reporting a silent success.',
      ],
    },
  },
  {
    version: '2.3.1',
    headline: 'Manually marking something watched no longer logs it to 1970',
    summary:
      'Films and episodes that Jellyfin marks watched through real playback have always synced with the correct date. But checking a title watched by hand, using the checkmark rather than actually playing it, could leave Jellyfin\'s own watched-date field set to January 1st 1970 instead of a real date, and the plugin trusted that value as-is. The fix treats an epoch-adjacent watched date the same way it already treats a missing one: there\'s no real watch date to log yet, so the sync waits rather than posting a bogus entry to Letterboxd or Serializd. Once Jellyfin records an actual playback date for the title, it syncs normally.',
    highlights: {
      fixes: [
        'Manually marking a film or episode watched no longer risks logging it to Letterboxd or Serializd with a "seen on 1970-01-01" date.',
        'Titles affected by this are simply skipped (not synced with a fabricated date) until Jellyfin records a real watch date, matching how the plugin already handles a missing watched date.',
      ],
    },
  },
  {
    version: '2.3.0',
    headline: 'Broken Letterboxd logins now pause themselves and tell you about it',
    summary:
      'When a Letterboxd password went stale, the plugin used to retry the login on every scheduled run, forever, and the only place you could find out was the plugin\'s own dashboard. On a set-and-forget server that meant weeks of silent failures, and the constant retries could even trip Letterboxd\'s rate limiting for accounts that still worked. Now, after three consecutive failed logins, syncing for that account pauses itself: no more login attempts, a warning lands in Jellyfin\'s activity log where admins actually look, and the account card in both dashboards shows a "login failing" badge. Re-save the account\'s credentials and syncing resumes on the next run, and nothing is lost in the meantime, the scheduled catch-up picks up any watches from while it was paused.',
    highlights: {
      new: [
        'After three consecutive failed Letterboxd logins, syncing for that account pauses automatically instead of retrying forever, protecting you from rate limiting.',
        'The pause is announced in Jellyfin\'s activity log, so admins see it without opening the plugin dashboard.',
        'Account cards on both the admin and user pages show a "Login failing · sync paused" badge with the date it started.',
        'Re-saving the account\'s credentials (or any successful login) resumes syncing; watches from the paused period are picked up by the next catch-up run.',
      ],
    },
  },
  {
    version: '2.2.0',
    headline: 'The review modal now shows the rating you already gave, and stars come in halves',
    summary:
      'Opening the review modal used to always start at "No rating", even when Jellyfin already knew exactly what you\'d rated the film or episode, whether you rated it in the Jellyfin web UI, a client app like Infuse, or through an earlier Jellyscribe review. Now the modal looks up your stored rating and pre-fills the stars, so you\'re editing your rating rather than re-entering it. The star widget also finally does half stars: click the left half of a star for a .5 rating, matching what Letterboxd itself supports. Ratings on Jellyfin\'s 10-point scale (like a 7/10) now display faithfully as 3.5 stars instead of not showing at all.',
    highlights: {
      new: [
        'The review modal pre-fills its stars from the rating already stored in Jellyfin, for films, shows, and individual episodes, whichever app you rated them in.',
        'Half-star ratings: click the left half of a star for .5 increments, on both the admin dashboard and the user page. Letterboxd reviews carry the half star as-is; Serializd reviews map it onto the 10-point scale.',
      ],
    },
  },
  {
    version: '2.1.2',
    headline: 'Sidebar link now reliably survives a Jellyfin restart',
    summary:
      'The sidebar link this plugin adds to Jellyfin\'s nav is injected by registering with the File Transformation plugin once, at Jellyfin startup. That registration was racing File Transformation\'s own startup init, so on some restarts (including scheduled ones) it silently failed and the link just never appeared, with nothing in the logs beyond a generic, undiagnosable error. Registration now retries a few times before giving up, and a real failure logs the actual cause instead of a useless wrapper message.',
    highlights: {
      fixes: [
        'Sidebar link registration now retries (with a short delay) instead of giving up after a single attempt, so it no longer depends on winning a startup race against the File Transformation plugin on every Jellyfin restart.',
        'A registration failure now logs the real underlying exception instead of reflection\'s generic "Exception has been thrown by the target of an invocation" message.',
      ],
    },
  },
  {
    version: '2.1.1',
    headline: 'Watchlist auto-requests now show up in your sync history',
    summary:
      'If your watchlist syncs with "auto-request via Jellyseerr" turned on, a successful request used to only ever show up in the server log, there was no way to see it from the dashboard itself. Now every title your watchlist successfully requests (film or TV) gets its own entry in your sync history, with the real title, right alongside your regular Letterboxd and Serializd activity. A request that fails shows up too, so if Jellyseerr rejects something you can see exactly what and when instead of wondering why your watchlist "isn\'t working."',
    highlights: {
      new: [
        'Sync history gets a new "Requested" entry for every title your watchlist successfully auto-requests via Jellyseerr, film or TV, with its real title.',
        'Failed auto-requests show up too, so a Jellyseerr rejection is visible on the dashboard instead of only in the server log.',
        'The Stats page counts these separately from your regular sync activity.',
      ],
    },
  },
  {
    version: '2.1.0',
    headline: 'Telemetry now covers TV syncs, the dashboard credit is back, and Logs actually refresh',
    summary:
      'A few follow-ups to the 2.0.0 rebrand. The admin and user dashboard headers lost their "by Lachlan Young and AI" credit line during the redesign, it\'s back. The opt-in anonymous telemetry, which only ever counted Letterboxd film syncs, now counts Serializd TV syncs and errors too, since that pipeline was never touched when TV support was built. And the Logs tab\'s Refresh button, which had been dumping a raw JSON blob into the panel instead of readable log lines, now shows the actual logs.',
    highlights: {
      fixes: [
        'Restored the "by Lachlan Young and AI" credit line in both the admin and user dashboard headers.',
        'Opt-in anonymous telemetry now tracks Serializd (TV) syncs and errors independently from the Letterboxd (film) side, instead of only ever reporting film activity.',
        'Fixed the Logs tab\'s Refresh button showing the raw JSON response instead of readable log lines.',
      ],
    },
  },
  {
    version: '2.0.0',
    headline: 'Renamed to Jellyscribe, and TV shows now sync to Serializd',
    summary:
      'Two things land together in this release: the plugin is renamed from "Letterboxd Sync" to Jellyscribe, and it now syncs TV shows to Serializd alongside films to Letterboxd, as a fully independent second diary.\n\nNothing about the rename requires any action, every linked account, sync history entry, and setting carries over untouched. The two diaries never affect each other, so a hiccup on one never touches the other. Details below.',
    highlights: {
      new: [
        'Renamed to Jellyscribe: new display name, new bookmark-ribbon mark, new site palette. Auto-updates in place, no reinstall or reconfiguration, same plugin GUID and repository URL as always.',
        'New TV / Serializd tab: link a Serializd account per Jellyfin user (email or username) with a Verify login button that confirms your credentials before saving.',
        'Real-time episode sync: finishing a TV episode logs it to your Serializd watched list automatically, matched by TMDb id so it works regardless of naming, dated to the actual watch time.',
        'Daily catch-up scheduled task ("Sync watched TV to Serializd") logs anything the real-time path missed, plus a Sync TV Now button to run it immediately.',
        'Series ratings and favorites sync to Serializd as a show rating and like; write a full review (with an optional rating) straight from the dashboard, at the show or a specific episode.',
        'Serializd watchlist sync: watchlisted shows mirror into a Jellyfin collection (browse) and the watchlisted seasons\' episodes into a playlist (play queue), with an opt-in Jellyseerr auto-request for anything missing.',
        'Reverse diary import: mark Jellyfin episodes played when they already show up in your Serializd diary.',
        'Serializd runs fully isolated from Letterboxd: films keep syncing to Letterboxd, and a failure on one service never blocks the other.',
      ],
    },
  },
  {
    version: '1.19.6',
    headline: 'Reverse sync now explains itself when it has nothing to do',
    summary:
      'If the "Import Letterboxd diary as Jellyfin watched" task ran and did nothing, it used to finish in a second and leave no trace in the log, so a switched-off toggle looked identical to a broken feature. The task now says why it skipped each user (for example, an account exists but diary import is not turned on, with a pointer to the setting), reports when your Letterboxd returned no films, and always ends with a one-line summary of what it checked. A stuck progress banner on the dashboard for empty diaries is also fixed. If reverse sync has not been working for you, update, run the task once, and the log will now tell you exactly why.',
    highlights: {
      fixes: [
        'The diary import task logs the reason whenever it skips a user, reports empty results, and always writes a completion summary, so a run that does nothing is explainable from the log alone.',
        'Fixed the dashboard progress banner staying active forever when a diary import found no films to import.',
      ],
    },
  },
  {
    version: '1.19.5',
    headline: 'Send logs to developer now tells you when there is nothing to send',
    summary:
      'A user sent a diagnostic bundle that arrived completely empty: the plugin had not logged anything recently, so there was nothing to collect, but the dialog still reported plain success and neither side could tell why the bundle was blank. Now the send dialog warns you when no plugin log lines were found (with a hint to reproduce the problem first and try again), and every bundle carries a small status line saying which log files were read and how many lines matched, so an empty bundle explains itself.',
    highlights: {
      fixes: [
        'The "Send logs to developer" dialog now shows a clear warning when the bundle contains no log lines, instead of reporting bare success on an empty send.',
        'Diagnostic bundles now include a collector status line (files read, lines matched, any read error), so support can tell an idle plugin from a broken log reader.',
      ],
    },
  },
  {
    version: '1.19.4',
    headline: 'Housekeeping: a warning-clean test build',
    summary:
      'No functional changes. The plugin\u2019s test suite compiled with seven compiler and analyzer warnings (a couple of async test methods that never awaited anything, one fire-and-forget mock setup, and a few assertions written the long way around). All seven are fixed, so the build is warning-clean again and future warnings stand out instead of drowning in noise. Nothing about syncing, accounts, or the dashboard changes in this release.',
    highlights: {
      fixes: [
        'Cleaned up all seven compiler and analyzer warnings in the test project; every touched test was verified to still fail when its assertion is broken, so no coverage was lost.',
      ],
    },
  },
  {
    version: '1.19.3',
    headline: 'Telemetry can now tell a quiet week from a plugin that never worked',
    summary:
      'A small improvement to the opt-in anonymous telemetry: alongside the existing per-week sync bucket, the weekly ping now includes a lifetime sync bucket. Until now, an instance reporting zero syncs in a week was indistinguishable from one where syncing never worked at all; the lifetime bucket separates the two, so setup problems can be spotted and fixed. Same privacy rules as everything else: a coarse bucket only (0, 1-10, 11-100, 100+), never exact numbers, and nothing is sent unless you opted in.',
    highlights: {
      improvements: [
        'Weekly telemetry pings gain a syncs_ever bucket: the lifetime count of successful syncs, reported in the same coarse buckets as all other counts. This distinguishes a healthy-but-idle instance from an install where syncing has never succeeded.',
      ],
    },
  },
  {
    version: '1.19.2',
    headline: 'Stored credentials are now encrypted at rest',
    summary:
      'Your Letterboxd password, session cookies, and Seerr API key are now encrypted before they are written to disk, instead of sitting in the plugin config file as plaintext. Nothing changes about how you use the plugin: existing saved credentials are picked up and upgraded automatically the next time they are saved, no re-entry needed.',
    highlights: {
      improvements: [
        'Letterboxd password, raw cookies, and Seerr API key are encrypted at rest using ASP.NET Core Data Protection, with the key kept alongside the plugin’s other data files.',
        'Existing plaintext values from earlier versions are read normally and silently upgraded to encrypted on the next save.',
      ],
    },
  },
  {
    version: '1.19.1',
    headline: 'AI transparency page and a code of conduct that fits',
    summary:
      'No functional changes to syncing. The project now documents openly how it is built: a new AI.md page explains that most of the plugin is AI-written under human direction and review, what tooling is used, and the checks every change passes before it ships. The Code of Conduct is rewritten to something honest for a one-person project, and the README links to all of it.',
    highlights: {
      improvements: [
        'New AI transparency page (AI.md in the repository) covering the AI tooling, the human review and CI gates, and the concrete scale of what has been built.',
        'Code of Conduct rewritten for a solo-maintained project, with reports directed to GitHub issues.',
        'Documentation cleanup: consistent punctuation across the README, website release notes, and code comments.',
      ],
    },
  },
  {
    version: '1.19.0',
    headline: 'A faster mirror joins your plugin catalog',
    summary:
      'A second repository entry for this plugin, an edge-cached mirror of the exact same manifest, is added to your Jellyfin catalog next to the existing GitHub one, a single time. Your GitHub entry is never touched, so updates keep working even if the mirror is unreachable, and if you would rather not have the mirror you can simply delete it: the plugin will not add it back. The mirror counts an anonymous, weekly-rotating hash per request so we can see roughly how many servers run the plugin; no IP addresses or personal data are ever stored, and this is entirely separate from the opt-in telemetry.',
    highlights: {
      improvements: [
        'The edge-cached manifest mirror is added to your plugin catalog alongside the GitHub entry (named after your existing entry with a "(mirror)" suffix, matching its enabled state). It serves the identical manifest and redirects downloads to the official GitHub releases. This runs once: deleting the mirror entry is respected and it is never re-added.',
        'The mirror no longer caches upstream GitHub errors at the edge, so a brief GitHub hiccup cannot pin a failed manifest response for other users.',
      ],
    },
  },
  {
    version: '1.18.5',
    headline: 'Anonymous install counter',
    summary:
      'No functional changes to syncing. This release adds an anonymous, privacy-preserving install counter so the developer can see roughly how many Jellyfin servers run the plugin. No personal data is collected: your IP address is never stored, and the anonymous weekly fingerprint used to avoid double-counting cannot be traced back to you or linked across weeks.',
    highlights: {
      new: [
        'Active installs are now counted from ordinary catalog update checks and release downloads served through an edge mirror of the official manifest. GitHub download totals were useless for this (re-downloads and auto-updates inflate them), and the opt-in telemetry only sees servers that enabled it.',
        'Uniqueness is approximated with a weekly-rotating pseudonymous fingerprint: your IP address is used transiently at the edge to compute it and is never stored in the counts. (Corrected later: the fingerprint uses a fixed secret salt, so whoever holds that salt could link a known IP\'s rows across weeks.) Unlike the opt-in telemetry, this counting is always on; it carries no other information about you, your server, or your library.',
      ],
      improvements: [
        'Release downloads in the plugin catalog now redirect through the mirror to the identical GitHub release file. Checksums are unchanged, so Jellyfin\'s integrity check on install and update passes exactly as before.',
      ],
    },
  },
  {
    version: '1.18.4',
    headline: 'Sync works again after a Letterboxd page change',
    summary:
      'Fixes a sync failure introduced when Letterboxd changed their website. Films stopped logging to your diary because the plugin could no longer read each film\'s identifier from its Letterboxd page. The plugin now reads the new page format, so watched films sync again.',
    highlights: {
      fixes: [
        'Letterboxd removed the film-page attributes the plugin\'s web-scraping fallback used to identify films, moving the identifiers into a new format on the poster element. The plugin now reads the new format (and still understands the old one). Users on the official API path were unaffected; anyone on the scraping fallback had every diary sync fail until this release.',
        'A live test now exercises the scraping path against the real Letterboxd site, so the next markup change of this kind is caught by CI instead of by user bug reports.',
      ],
    },
  },
  {
    version: '1.18.3',
    headline: 'More precise error reporting in telemetry',
    summary:
      'If you have opted in to the anonymous telemetry, plugin errors are now sorted into more specific categories instead of a single generic "other" bucket. Rate-limiting, Letterboxd server outages, failed writes back to Letterboxd, and page-parsing problems are each labelled distinctly. This is purely a labelling improvement: no behaviour changes, and nothing new about you or your library is collected.',
    highlights: {
      improvements: [
        'Anonymous telemetry now distinguishes four previously-uncategorised error types: rate-limiting (being throttled by Letterboxd), server errors (Letterboxd returning a 5xx), write failures (a watch or review that could not be logged after retries), and parsing errors (a Letterboxd page that no longer matches what the plugin expects). Previously these all counted as "other".',
      ],
      fixes: [
        'Fixed the error-state recovery so that, after a successful sync, every error category is cleared. One category was previously skipped, which could leave a stale "still failing" flag set.',
      ],
    },
  },
  {
    version: '1.18.2',
    headline: 'Jellyseerr is now called Seerr',
    summary:
      'Jellyseerr and Overseerr have merged into a single project called Seerr. The plugin now uses the new name everywhere you see it, in the settings page, your personal account page, and the documentation, so the labels match what you see in the app you are connecting to. Nothing about the integration changes: your existing Seerr URL, API key, and per-account settings are kept, and there is nothing to reconfigure.',
    highlights: {
      improvements: [
        'The "Jellyseerr integration" settings, the auto-request and watchlist-mirror options, and the connection-test messages now all read "Seerr" to match the renamed project. Your saved URL, API key, and account settings carry over untouched.',
      ],
    },
  },
  {
    version: '1.18.1',
    headline: 'Internal: test analytics in CI',
    summary:
      'No functional changes. CI now uploads JUnit test results to Codecov Test Analytics so flaky tests and failures are tracked across releases. Nothing in the plugin itself changes.',
    highlights: {
      improvements: [
        'CI emits a JUnit test report and uploads it to Codecov Test Analytics for flaky-test detection and failure reporting. Developer-facing only; no change to plugin behaviour.',
      ],
    },
  },
  {
    version: '1.18.0',
    headline: 'Clearer guidance when Letterboxd login fails',
    summary:
      'Letterboxd often blocks plain username/password login with a Cloudflare or reCAPTCHA challenge, and accounts with two-factor authentication can never log in that way at all. Telemetry showed this is the single most common reason a sync fails on a fresh install. This release makes raw cookies the clearly recommended way to authenticate, warns you when you save an account without them, and turns the cryptic two-factor login failure into a message that tells you exactly what to do.',
    highlights: {
      improvements: [
        'Raw Cookies are now presented as the recommended way to authenticate, on both the admin settings page and your personal account page, with a short explanation of why password-only login is unreliable (Cloudflare/reCAPTCHA challenges, and two-factor accounts that cannot use it at all).',
        'Saving an enabled account that has a password but no raw cookies now shows a non-blocking warning, so you find out before the first sync fails rather than after. The save still goes through.',
      ],
      fixes: [
        'Accounts with two-factor authentication enabled now fail login with a clear, actionable message ("this account has two-factor authentication enabled, add raw cookies including cf_clearance, or disable 2FA") instead of a vague generic login error. Password login genuinely cannot complete a 2FA challenge, so the plugin now says so and points you at the fix.',
      ],
    },
    upgradeNotes:
      'If your syncs have been failing with auth errors, open your Letterboxd Sync account settings and paste Raw Cookies (including cf_clearance) from a browser already signed in to Letterboxd. This is now the recommended setup, especially for accounts with two-factor authentication.',
  },
  {
    version: '1.17.0',
    headline: 'Send your logs to the developer in one click',
    summary:
      'Reporting a problem used to mean digging through Jellyfin log files and pasting the right lines into a GitHub issue. Now there is a "Send logs to developer" button on the Logs tab: it packages your recent Letterboxd Sync activity, uploads it privately, and hands you a short reference code to quote in your bug report. It is opt-in every single time you use it, shows you exactly what will be sent before anything leaves your server, and is upfront that logs, unlike the anonymous telemetry, can identify you. This release also tightens who on your server is allowed to read those logs.',
    highlights: {
      new: [
        'Send logs to developer: open Dashboard, then Letterboxd Sync, then the Logs tab, and click "Send logs to developer". The plugin bundles the recent Letterboxd Sync log lines (the same ones already shown on that tab) and uploads them, then gives you a short reference code like LBX-7Q2F9K. Quote that code when you open a GitHub issue and the developer can pull your exact logs to diagnose the problem, no more copy-pasting walls of text.',
        'See exactly what is sent, before it is sent. The confirmation step has a "Preview exactly what is sent" view that renders the real log lines and the telemetry snapshot, byte for byte what gets uploaded. You can also add a short note describing what went wrong, which is often the single most useful thing for diagnosis.',
        'Honest about privacy. Unlike the anonymous weekly telemetry, logs are NOT anonymous: they can contain a Letterboxd username or film titles, and the bundle is linked to the telemetry ID for that install. The dialog says this plainly, nothing is sent until you confirm, passwords and cookies and auth tokens are never logged, and it works whether or not anonymous telemetry is turned on.',
        'Your logs are not kept forever. Uploaded bundles are stored privately and automatically deleted after 90 days.',
      ],
      improvements: [
        'Stronger log access control. The plugin log endpoint now requires administrator access. Previously any signed-in Jellyfin user on the server could read the Letterboxd Sync logs, which name every linked Letterboxd account and watched film. On shared or family servers this is now admin-only.',
        'Better diagnostics when you do send logs: full multi-line error traces are now captured intact (they were previously cut into fragments), log text is cleaned of stray terminal control codes, and the bundle always records your Jellyfin version.',
      ],
    },
    upgradeNotes:
      'Nothing changes unless you choose to use the new button. To send logs, go to Dashboard, then Letterboxd Sync, then the Logs tab, and click "Send logs to developer" (admin only). Anonymous telemetry, if you enabled it in 1.16.0, is unaffected.',
  },
  {
    version: '1.16.0',
    headline: 'Anonymous opt-in telemetry',
    summary:
      'Entirely opt-in and off by default. If you enable it, one small anonymous ping a week tells the project which features are actually used, so roadmap decisions stop being guesses. Nothing else in the plugin changes.',
    highlights: {
      new: [
        'Anonymous usage telemetry, opt-in via a one-time dashboard banner or the Settings checkbox. The payload is minimal and bucketed: version numbers, which features are enabled, and rough size buckets. Never film titles, usernames, IPs, or exact numbers. A "Preview exact JSON" button in Settings shows precisely what would be sent, and the full payload is documented in the README.',
        'Identified only by a random instance ID generated when you opt in, with a one-click Regenerate button that unlinks future pings. The preview doubles as a diagnostic bundle for bug reports, with an honest warning about what pasting it publicly reveals.',
        'An extra anonymous ping fires when sync errors start occurring (capped at one per day), powering an automated canary that compares error rates across releases and catches fleet-wide breakage, like a Letterboxd endpoint change, before bug reports arrive.',
      ],
    },
  },
  {
    version: '1.15.4',
    headline: 'Required update for Jellyfin 10.11.9 and 10.11.10 servers',
    summary:
      'Fixes releases v1.14.1 through v1.15.3 failing to load on Jellyfin 10.11.9/10.11.10 (the plugin was compiled against a newer Jellyfin SDK than those servers ship). If your plugin recently showed as disabled or "malfunctioned", update to this version. No feature changes.',
    highlights: {
      fixes: [
        'Jellyfin assemblies carry full per-patch versions, so a plugin compiled against the 10.11.11 SDK silently fails to load on 10.11.10 and older, while the catalog still offered those releases to 10.11.9+ servers. This release is compiled against the 10.11.9 SDK, matching the advertised minimum for the first time, and the catalog metadata for the affected versions has been corrected.',
        'New build policy, enforced by CI: the plugin is always compiled against the oldest supported Jellyfin SDK, and the minimum only rises deliberately when a newer Jellyfin API is genuinely needed, never via a routine dependency update.',
      ],
    },
    upgradeNotes:
      'If the plugin is currently disabled on your server: update to v1.15.4 in the catalog (or reinstall), then restart Jellyfin.',
  },
  {
    version: '1.15.3',
    headline: 'Maintenance: telemetry design spec',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Adds the OpenSpec change proposal for upcoming anonymous, opt-in usage telemetry: minimal bucketed payload, default off with a one-time prompt, full payload preview in settings, and a fleet canary that auto-detects regressions. Spec only; no telemetry code ships in this release.',
      ],
    },
  },
  {
    version: '1.15.2',
    headline: 'Maintenance: deterministic test teardown for manual-sync endpoints',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Fixes an intermittent CI failure: the manual sync endpoints return 202 and run in the background, and a background sync from one test could still hold the shared sync gate when the next test ran, turning an expected 400 into a 409. The test harness now waits for the spawned sync to finish before the next test starts.',
      ],
    },
  },
  {
    version: '1.15.1',
    headline: 'Maintenance: letterboxdsync.dev release notes backfill',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'The Releases page on letterboxdsync.dev now has structured highlights for v1.13.4 through v1.15.0, which had shipped without entries.',
      ],
    },
  },
  {
    version: '1.15.0',
    headline: 'Jellyseerr request backfill for already-available watchlist films',
    summary:
      'New opt-in per-account setting "Also backfill requests for already-available watchlist films" (off by default; requires Auto-request). Default behaviour is unchanged for everyone who leaves the box off.',
    highlights: {
      new: [
        'Watchlist auto-request already creates attributed Jellyseerr requests for films missing from the library, but a film that is on a watchlist and entered the library through another path (manual Radarr add, an import list, or a deleted request) ended up Available with no request record, untraceable. With backfill on, the whole watchlist is considered and available-but-unrequested titles still get an attributed request, so "who requested this?" is always answerable.',
        'Per-user dedup: a backfill request is skipped only when the title is blocklisted or this user already has a request for it. Verified against Jellyseerr 3.2.0 that a backfill request on available media succeeds without triggering a re-download.',
      ],
    },
  },
  {
    version: '1.14.1',
    headline: 'Maintenance: Jellyfin SDK 10.11.11',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Jellyfin.Controller and Jellyfin.Model updated from 10.11.10 to 10.11.11 (a one-change upstream patch release; no ABI impact, targetAbi unchanged). First update delivered by the Dependabot watch introduced in v1.13.3.',
      ],
    },
  },
  {
    version: '1.14.0',
    headline: 'Stops phantom daily rewatches and endless retries of permanently-failing films',
    summary:
      'Fixes two scheduled-sync dedup bugs reported by a plugin user, one of which re-logged the same film to Letterboxd roughly every other day.',
    highlights: {
      fixes: [
        'Films marked played on Jellyfin with no last-played date (marked watched manually, or before Jellyfin tracked dates) defaulted their viewing date to "today", which drifted every run and slipped past all duplicate checks, posting a phantom rewatch to the Letterboxd diary on a rolling basis. Scheduled sync now skips these films entirely until a real play date exists.',
        'A film whose sync always fails (for example one Letterboxd cannot match) was re-queued at the head of the queue on every run, forever. Sync now abandons a film after 3 consecutive failures and a successful sync resets the counter.',
      ],
    },
  },
  {
    version: '1.13.4',
    headline: 'Maintenance: test coverage expansion',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Test suite expanded to 89.8% line / 81.4% branch coverage, adding coverage for sync-gate contention, named-account targeting, skip-previously-synced filtering, stop-on-failure, and the local-history duplicate backstop.',
      ],
    },
  },
  {
    version: '1.13.3',
    headline: 'Maintenance: Dependabot watches the Jellyfin SDK, version-gate links targetAbi to minor bumps',
    summary:
      'Two preventative measures aimed at the next ABI surprise. No plugin behaviour changes.',
    highlights: {
      improvements: [
        "Dependabot now opens a PR the day Jellyfin ships a new Jellyfin.Controller / Jellyfin.Model patch, so the next 10.11.x ABI break is caught by CI on our timeline rather than via a user report (incident: v1.13.0 only existed because 10.11.9's silent IUserManager.Users removal surfaced as a bigrichwood bug report).",
        'version-gate now refuses a patch-only bump when targetAbi.txt changes in the same PR. Moving the Jellyfin compatibility floor stops a cohort of users from being offered the next plugin update; that warrants at least a minor version bump so the change is visible in the release notes.',
      ],
    },
  },
  {
    version: '1.13.2',
    headline: 'Release notes pipeline: prose-style changelogs back, sourced from the PR body',
    summary:
      "Restores the v1.0–v1.12 manifest-changelog tone after v1.13.0 and v1.13.1 drifted into incident-report prose and a conventional-commit subject line respectively. Backfills both on letterboxdsync.dev's Releases page.",
    highlights: {
      improvements: [
        "release.yml now fetches the merged PR's body via the GitHub API and extracts text between a '## Release notes' header and the next H2 as the manifest changelog. Falls back to the PR title only if the section is missing.",
        "New .github/pull_request_template.md primes every PR with the section so it's the path of least resistance.",
        'Backfills the manifest changelog for v1.13.0 and v1.13.1 to match the single-paragraph user-facing prose of v1.0 through v1.12.',
        'Backfills letterboxdsync.dev with structured Release notes entries for v1.13.0 and v1.13.1 (previously missing).',
      ],
    },
  },
  {
    version: '1.13.1',
    headline: 'Maintenance: stronger release pipeline',
    summary: 'No plugin behaviour changes.',
    highlights: {
      improvements: [
        'Every PR now bumps the version (gated by a required CI check) and auto-ships on merge to main, replacing the manual `git tag` step.',
        'PR titles must follow Conventional Commits (`feat:`, `fix:`, `chore:`, `docs:`, `ci:`, `refactor:`, `test:`, `perf:`, `build:`, `style:`). Enforced by CI on every PR.',
        'letterboxdsync.dev rebuilds on every release via a `workflow_run` trigger, so the Releases page is never stale (the manifest auto-commit otherwise cannot fire push-based workflows).',
      ],
    },
  },
  {
    version: '1.13.0',
    headline: 'Required update for Jellyfin 10.11.9 and newer',
    summary: 'Fixes #46. Restores sync on the new SDK ABI; no other plugin behaviour changes.',
    highlights: {
      fixes: [
        'Jellyfin 10.11.9 removed the `Users` property from `IUserManager` and replaced it with a `GetUsers()` method. The plugin was compiled against the 10.11.0 SDK, so on Jellyfin 10.11.9 and 10.11.10 every read of the user list threw `MissingMethodException`: the dashboard Stats and History endpoints returned 500, and both scheduled sync tasks failed immediately. Recompiled against 10.11.10 and rewrote all seven `_userManager.Users` call sites to use `GetUsers()`.',
      ],
      breaking: [
        '`targetAbi` is now `10.11.9.0`. The Jellyfin plugin catalog will only offer this release to servers running 10.11.9 or newer; Jellyfin 10.11.0 through 10.11.8 servers stay on v1.12.0 (which still works for them).',
      ],
    },
    upgradeNotes:
      'If your Jellyfin server is on 10.11.0 through 10.11.8 the catalog will not offer v1.13.0 by design; upgrade Jellyfin to 10.11.9 or newer to receive this and future plugin updates.',
  },
  {
    version: '1.12.0',
    headline: 'Multi-account support: one Jellyfin user, many Letterboxd accounts',
    summary: 'Shared TV logins (e.g. two people on the same family Jellyfin profile) can now each have their own Letterboxd diary, ratings, and watchlist.',
    highlights: {
      new: [
        'A Jellyfin user can link multiple Letterboxd accounts. Auto-sync (real-time and scheduled) fans out across every enabled account; one failing account never blocks the others.',
        'The sidebar "My Letterboxd" page now has feature parity with the admin plugin page for per-account management: add, remove, reorder, test, and configure every account that belongs to your Jellyfin user, without needing admin access. (Admin-only things like the Jellyseerr server URL and editing other users\' accounts stay in the admin page.)',
        "Per-account watchlist playlists. Default name is 'Letterboxd Watchlist ({letterboxdUsername})' so two accounts on one Jellyfin user get two separate playlists, with an optional per-account name override.",
        "Review modal has a 'Post as' account selector when more than one account is enabled, defaulting to posting on all of them.",
        'New IsPrimary flag on each account: used to break rating conflicts on diary import and as the preselected option in the review modal.',
      ],
      improvements: [
        'Manual API endpoints (/Sync, /SyncWatchlist, /Review) accept an optional letterboxdUsername selector. Omit it to fan out across all enabled accounts.',
        'Diary import unions played-state across all linked accounts and merges ratings with the primary account winning conflicts. Existing Jellyfin ratings are still preserved.',
      ],
      breaking: [
        "Single-account users will see a new 'Letterboxd Watchlist (yourusername)' playlist created on the next watchlist sync. The old 'Letterboxd Watchlist' playlist is left untouched so you can delete or migrate at your leisure.",
      ],
    },
  },
  {
    version: '1.11.3',
    headline: 'Plain-English error and docs when Cloudflare 403s with cookies already set',
    highlights: {
      improvements: [
        "The TMDb-lookup 403 exception now names the three real causes (cf_clearance expired, often around 30 minutes; pinned to a different IP than the Jellyfin server; or rejected by Cloudflare's TLS fingerprinting) and points at the README, instead of suggesting raw cookies that the user has already pasted.",
        "README has a new 'Still 403ing after pasting Raw Cookies and a matching User-Agent' subsection under 'Cloudflare issues' with a concrete fix per cause. Addresses the dead-end case reported in #34.",
      ],
    },
  },
  {
    version: '1.11.2',
    headline: 'Stop phantom rewatches on diary-imported films',
    highlights: {
      fixes: [
        "With diary-import enabled, the daily sync was posting phantom rewatch entries to Letterboxd for films you'd only marked played via diary import (never actually watched on Jellyfin). The runner now waits for a real Jellyfin playback before logging the rewatch.",
      ],
      improvements: [
        'Install instructions now call out the File Transformation plugin as a prerequisite for the in-sidebar Letterboxd link.',
      ],
    },
  },
  {
    version: '1.11.1',
    headline: "Don't request the wrong movie when watchlisting a TV show",
    highlights: {
      fixes: [
        'Watchlisting a TV show on Letterboxd no longer auto-requests an unrelated movie in Jellyseerr. TMDb has independent ID namespaces for movies and TV (e.g. tv/198102 = Hijack, movie/198102 = Cutie Honey Flash); the link extractor was treating every tmdb link as a movie ID. Now skips /tv/ links, with regression coverage.',
      ],
    },
  },
  {
    version: '1.11.0',
    headline: 'Bidirectional rating sync and in-dashboard logs',
    highlights: {
      new: [
        'Bidirectional rating sync. Dashboard reviews mirror their star rating into Jellyfin (always overwrites), and the daily diary import seeds Jellyfin ratings from Letterboxd for films that don\'t yet have a Jellyfin rating (anti-clobber).',
        'In-dashboard Logs tab with per-user and free-text filters, copy-to-clipboard, and download-as-.log for support requests.',
      ],
      improvements: [
        'Diary import switched from /log-entries to /films?memberRelationship=Watched so films you rated on Letterboxd without logging a watch now sync too.',
        'Privacy hardening: review text is no longer logged.',
      ],
    },
  },
  {
    version: '1.10.1',
    headline: 'Maintenance: bump GitHub Actions runtime',
    highlights: {
      improvements: [
        'Bumped GitHub Actions versions (checkout v6, setup-dotnet v5, action-gh-release v3) to remain on a supported Node version. No plugin behaviour changes.',
      ],
    },
  },
  {
    version: '1.10.0',
    headline: 'Watchlist mirroring into Jellyseerr',
    highlights: {
      new: [
        "Mirror Letterboxd watchlist into Jellyseerr's user watchlist. Per-account toggle, two-way sync, movies-only so manually-added Jellyseerr TV is safe.",
        "On-demand 'Sync Watchlist Now' button on the plugin dashboard so you don't have to wait for the daily run.",
      ],
      improvements: [
        "Pre-flight check against Jellyseerr's media status eliminates duplicate requests for already-pending/processing/available titles.",
        'Per-user watchlist sync runner extracted with a shared SyncGate so diary and watchlist syncs serialise.',
      ],
    },
  },
  {
    version: '1.9.1',
    headline: 'Local-history backstop against Cloudflare-induced duplicates',
    summary: 'Addresses #21.',
    highlights: {
      fixes: [
        'Refuse to MarkAsWatched when sync history shows a recent successful sync that does not pass the rewatch threshold. Fixes the Cloudflare-failed-validator path that was creating real Letterboxd diary duplicates.',
      ],
      improvements: [
        'Paginated dashboard history table (100 per page) now that the 500-entry cap is gone.',
      ],
    },
  },
  {
    version: '1.9.0',
    headline: 'Skip already-synced films and let non-admins Run Sync Now',
    summary: 'Fixes #20.',
    highlights: {
      new: [
        'Non-admin users can now Run Sync Now (triggers their own account only).',
        'Per-account stop-on-failure toggle to halt the moment Letterboxd anti-flooding triggers.',
      ],
      improvements: [
        "Skip already-synced films using local sync history so we don't burn Cloudflare quota on duplicate checks.",
        'Prioritise previously-failed and skipped films first.',
        'Explicit info-level logging for every skip with a reason.',
      ],
    },
  },
  {
    version: '1.8.0',
    headline: 'Jellyseerr auto-request for unmatched watchlist films',
    highlights: {
      new: [
        'Jellyseerr auto-request for unmatched watchlist films, with per-user attribution via Jellyfin User ID.',
      ],
      fixes: [
        'Dedup fix: the watchlist playlist no longer accumulates duplicate entries on each run.',
      ],
    },
  },
  {
    version: '1.7.1',
    headline: 'Watchlist sync no longer returns 0 films via official API',
    highlights: {
      fixes: [
        'A redundant `member` query param was causing Letterboxd to return empty items. Removed.',
      ],
    },
  },
  {
    version: '1.7.0',
    headline: 'Per-account User-Agent override',
    highlights: {
      new: [
        'Per-account User-Agent override so Cloudflare cookies copied from any browser (Chrome, Safari, etc.) work without UA mismatch.',
      ],
    },
  },
  {
    version: '1.6.1',
    headline: 'Reviews work again via official API',
    summary: 'Fixes #12.',
    highlights: {
      fixes: [
        'Resolve film LID from TMDb ID instead of slug when posting reviews via the official API.',
      ],
    },
  },
  {
    version: '1.6.0',
    headline: 'Official Letterboxd API integration with scraping fallback',
    highlights: {
      new: [
        'Official Letterboxd API is now the primary path, with web scraping as a fallback. Eliminates the Cloudflare 403 errors that were blocking syncs.',
      ],
    },
  },
  {
    version: '1.5.0',
    headline: 'User self-service account setup and standalone user page',
    highlights: {
      new: [
        'User self-service account setup, users link their own Letterboxd account without admin help.',
        'Sidebar link for all users.',
        'Test connection button on the account form.',
        'Standalone user page via File Transformation injection.',
      ],
    },
  },
  {
    version: '1.4.0',
    headline: 'Architecture refactor, TMDb cache, progress dashboard',
    highlights: {
      new: [
        'Progress dashboard showing sync state at a glance.',
        'TMDb cache for repeated lookups.',
      ],
      improvements: [
        'Architecture refactor for clearer separation of HTTP, auth, scraping, and diary writes.',
        'Cloudflare resilience improvements.',
        'Watchlist cleanup pass.',
      ],
      fixes: ['Diary import fix.'],
    },
  },
  {
    version: '1.3.0',
    headline: 'Real-time playback sync',
    highlights: {
      new: [
        'Real-time playback sync via PlaybackHandler, diary entries land within seconds of credits rolling.',
      ],
      improvements: ['Automatic session re-auth on 401.'],
    },
  },
  {
    version: '1.2.1',
    headline: 'Sync history persists across version upgrades',
    highlights: {
      fixes: ['Fix sync history persistence across version upgrades.'],
    },
  },
  {
    version: '1.2.0',
    headline: 'Star ratings in reviews and rewatch date picker',
    highlights: {
      new: [
        'Star ratings in reviews.',
        'Rewatch date picker.',
        'Better error display in the dashboard.',
      ],
      improvements: ['Cloudflare retry on review posting.'],
      fixes: ['Sync history persistence fix.'],
    },
  },
  {
    version: '1.1.0',
    headline: 'Dashboard, watchlist sync, diary import, reviews',
    highlights: {
      new: [
        'Dashboard with sync history.',
        'Watchlist sync.',
        'Diary import.',
        'Reviews from the dashboard.',
        'Rating sync.',
        'Rewatch detection.',
      ],
      improvements: ['Cloudflare backoff.'],
    },
  },
  {
    version: '1.0.0',
    headline: 'Initial release',
    highlights: {
      new: [
        'Real-time sync on playback completion.',
        'Scheduled catch-up sync.',
        'Multi-user support.',
        'Duplicate detection.',
        'Retry with exponential backoff.',
      ],
    },
  },
];

export const notesByVersion: Record<string, ReleaseNotes> = Object.fromEntries(
  releaseNotes.map((n) => [n.version, n]),
);
