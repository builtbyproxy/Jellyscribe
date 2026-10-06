# in-app-page Specification

## Purpose
Opens Jellyscribe's user dashboard as a page inside Jellyfin's web client at #/jellyscribe, with no reload, the way Jellyfin Enhanced's Bookmarks works, reachable from the sidebar on Jellyfin 10.11 and the avatar menu on Jellyfin 12, falling back to the configuration page whenever it cannot mount. Added in 2.10.0 (#138).
## Requirements
### Requirement: Jellyscribe opens as a page inside Jellyfin's web client
Navigating to the `#/jellyscribe` route SHALL show the Jellyscribe user dashboard inside Jellyfin's page container without reloading the web client, keeping Jellyfin's header and menu, on Jellyfin 10.11 with or without a server base URL and on Jellyfin 12.

#### Scenario: Open from the sidebar
- **WHEN** a signed-in user on Jellyfin 10.11 clicks Jellyscribe in the sidebar
- **THEN** the location becomes `#/jellyscribe` under the same path (base URL kept), the user's diaries, accounts and activity are shown, and the web client does not reload

#### Scenario: Open from the avatar menu on Jellyfin 12
- **WHEN** a signed-in user on Jellyfin 12 opens the avatar menu
- **THEN** a Jellyscribe item appears next to Settings, and choosing it shows the same in-app page

#### Scenario: Deep link
- **WHEN** a signed-in user loads or refreshes the web client at `#/jellyscribe`
- **THEN** the Jellyscribe page is shown

### Requirement: Leaving the page restores Jellyfin
Navigating away from `#/jellyscribe` by any means SHALL hide the Jellyscribe page and leave Jellyfin's own page showing, and the dashboard SHALL not remain in the document while hidden.

#### Scenario: Back button
- **WHEN** the user opened Jellyscribe from the home screen and presses back
- **THEN** the home screen is shown and the Jellyscribe page is hidden

#### Scenario: Menu navigation
- **WHEN** the Jellyscribe page is showing and the user picks a library or Home from the menu
- **THEN** that page is shown and the Jellyscribe page is hidden

### Requirement: The dashboard works the same in the page
The in-app page SHALL render the same user dashboard as the configuration page, with its sections, account editor and review modal working.

#### Scenario: Edit an account from the in-app page
- **WHEN** the user opens Accounts in the in-app page and saves an account
- **THEN** the change is saved exactly as from the configuration page

### Requirement: Failure falls back to today's page
If the in-app page cannot be shown (the dashboard cannot be fetched, Jellyfin's page container is missing, or another copy of the dashboard is already in the document), opening Jellyscribe SHALL navigate to the configuration page instead, and the configuration page SHALL keep working as before.

#### Scenario: Dashboard already open as a configuration page
- **WHEN** the configuration page's dashboard is still in the document and the user opens Jellyscribe
- **THEN** the user is taken to the configuration page rather than a second copy being mounted

