# Moonbase, Lica's fork

This is Licaa21's fork of the [Moonfin server plugin](https://github.com/Moonfin-Client/Plugin) (Moonbase, for Jellyfin 10.10 and newer). It is made for one server and is not a project for everyone: upstream's plugin is the one to use unless you want what is below.

**Credit.** Almost all of this plugin is not mine. Moonfin and Moonbase were created by **RadicalMuffinMan** ([GitHub](https://github.com/RadicalMuffinMan)) and are built by the [Moonfin contributors](https://github.com/Moonfin-Client/Plugin/graphs/contributors) at [Moonfin-Client/Plugin](https://github.com/Moonfin-Client/Plugin); the web app comes from [Moonfin-Core](https://github.com/Moonfin-Client/Moonfin-Core). This fork only adds the small set of changes listed below on top of their work, and it is published under the same GPL-3.0 license. All credit for Moonfin goes to them.

**Compatibility.** This fork is made for, and supported only with, my own fork of the Moonfin Smart TV app: [Licaa21/Smart-TV](https://github.com/Licaa21/Smart-TV) (Samsung Tizen and LG webOS). It is not tested or supported with any other Moonfin client (the official Smart TV, Android TV, mobile, desktop or Emby apps). If you use those, install upstream's plugin instead.

**What differs from upstream**

- The server holds the TMDB key and serves the TMDB titles, descriptions, season episodes, recommendations and air dates that a client would otherwise need its own key for (`/Moonfin/Tmdb/Details`, `Season`, `Recommendations`).
- The Moonbase User defaults tab holds a default for every setting of the Moonfin Smart TV app (subtitle styling and the second subtitle track, audio codec order, accent colors, skip overlay and more). Each sits in the category of the app's own screen, next to the settings every Moonfin app shares, and can be applied to users like the other defaults.
- The download carries the Moonfin web app (the Moonfin button in Jellyfin web, served at `/Moonfin/Web/`), taken unchanged from upstream's 2.5.0 release because upstream adds it to its zip by hand. Its source is [Moonfin-Core](https://github.com/Moonfin-Client/Moonfin-Core). That makes the download about 66 MB.

**What does not work in the fork**

- Push notifications through Moonfin's hosted relay. That relay needs a key that only upstream's own builds carry, and a key cannot go into a public download. To get push you need your own Firebase project: put its service account in Moonbase (Push Service Account). Whether that reaches the Moonfin app from the App Store is untested.

**Install (replaces upstream's Moonbase in place, with its settings)**

1. In Jellyfin: Dashboard, Plugins, Repositories, add `https://raw.githubusercontent.com/Licaa21/Plugin/refs/heads/main/manifest.json`.
2. Uninstall upstream's Moonbase first if it is installed, then install Moonbase from the new catalog entry and restart Jellyfin. The plugin keeps the same id, so what you had configured stays.

Releases are kept and numbered as upstream's version plus a fork digit (2.4.0.1, 2.4.0.2 ...). Upstream's README follows unchanged.

---

<h1 align="center">Moonbase, the server plugin for Moonfin!</h1>
<h3 align="center">Moonbase is the server plugin that powers the Moonfin experience on both Jellyfin and Emby.</h3>

---

<p align="center">
   <img width="1920" height="1080" alt="Moonbase" src="https://github.com/user-attachments/assets/5d67e8d0-5972-49f2-89d5-376357c8997b" />
</p>

[![License](https://img.shields.io/github/license/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin) [![Release](https://img.shields.io/github/release/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin/releases) [![Github Downloads](https://img.shields.io/github/downloads/Moonfin-Client/Plugin/total?label=Downloads)](https://github.com/Moonfin-Client/Plugin/releases) 


## What is Moonbase?

Moonbase is a server plugin for **Jellyfin and Emby** that gives every Moonfin client its shared backbone. It syncs your settings across devices, hosts the Moonfin web app right on your server, adds extra rating sources, connects your library to Seerr, and gives admins server-wide defaults and tools.

> **Recommended:** if you use any Moonfin client, install Moonbase on your server for the best experience.

This repo ships two plugins from one place: a Jellyfin plugin under `Jellyfin/` and an Emby plugin under `Emby/`.

## Installation

Moonbase needs **Jellyfin 10.10 or newer** or **Emby 4.8 or newer**. The wiki's [Installation](https://github.com/Moonfin-Client/Plugin/wiki/Installation) page has the steps plus updating and uninstalling.

### Jellyfin

The plugin repository is the recommended route, because it updates through the catalog like any other plugin:

1. Open the Jellyfin Dashboard, then go to Administration > Plugins > Repositories.
2. Add a repository:
   - **Name:** `Moonbase`
   - **URL:** `https://raw.githubusercontent.com/Moonfin-Client/Plugin/refs/heads/master/manifest.json`
3. Go to Catalog, find **Moonbase**, and install it.
4. Restart Jellyfin.

<details>
<summary><b>Advanced:</b> manual install</summary>

1. Download the latest `Moonfin.Server-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases)
2. Extract it into your Jellyfin plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/jellyfin/plugins/Moonfin/` |
   | Docker | `/config/plugins/Moonfin/` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Moonfin\` |
3. Restart Jellyfin


</details>

### Emby

The Emby plugin is a drop-in zip, not a catalog plugin:

1. Download the latest `Moonfin.Emby-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases).
2. Extract its contents (`Emby.Plugins.Moonfin.dll`, `SharpCompress.dll`, and the `web` folder) into your Emby plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/emby/plugins/` |
   | Docker | `/config/plugins/` |
   | Windows | `%AppData%\Emby-Server\programdata\plugins\` |
3. Restart Emby.

## Opening the Moonfin Web App

Moonbase serves the Moonfin web app on your own server, so you can open and bookmark it directly:

`https://your-server-host/Moonfin/Web/`

> **Not loading after a fresh install?** Run the **Moonfin Startup** task once (Dashboard > Scheduled Tasks), then refresh your browser.

<details>
<summary><b>Advanced:</b> a Moonfin button in the stock Jellyfin web header</summary>

On Jellyfin, you can add a one-click Moonfin button to the stock web header (next to your user icon) by installing the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin:

1. Add its plugin repository: `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
2. Install **File Transformation** from the catalog and restart Jellyfin.
3. Force refresh your browser.

To hide the button while keeping the plugin, add this to Branding > Custom CSS:

```css
.headerMoonfinButton { display: none !important; }
```

On Emby, the web app is served the same way at `/Moonfin/Web/`, but there's no header button because Emby has no equivalent injection path.

<img width="1521" height="164" alt="Moonfin header button" src="https://github.com/user-attachments/assets/bcb69e4b-edbe-4d1f-b9f1-dc81822d55d9" />

</details>

## Features

- **Cross-device settings sync**, so your preferences follow you between web, TV, mobile, and desktop, with an optional per-device profile for desktop, mobile, and TV.
- **The Moonfin web app** hosted at `/Moonfin/Web/`, running side by side with the stock web interface.
- **Media bar and home screen data** resolved on the server and shared across clients.
- **Recommendations scored on the server** (Jellyfin), matching similar titles on genres, tags, people, studios, franchise, release year and rating, and only showing what each user is allowed to see. On Jellyfin 12 the same scoring reaches every client, including the stock web interface.
- **Extra rating sources** through MDBList and TMDB, with the API keys kept on the server.
- **TMDB extras for the apps**: user reviews, a person's IMDb page and social accounts, and the overview, logo and trailer for titles outside your library, fetched with the server's TMDB key or the user's own.
- **Next air dates** for series, taken from the Sonarr your Seerr is connected to, or from TMDB through the server when there's no Sonarr.
- **Anime filler and recap markers** that badge episodes as Filler, Mixed, Recap or canon using AnimeFillerList and MyAnimeList, plus Subbed and Dubbed pills read from the audio tracks in your own files, or the shorter Sub and Dub. Shows match even when your library titles aren't in English. No account or API key needed.
- **A seasonal home row** of holiday movies your users own, plus Seerr suggestions, around New Year's, Valentine's Day, Easter, Pride, Halloween, Thanksgiving, Christmas, Lunar New Year and Diwali, following each user's parental limit.
- **Seerr integration** with a built-in proxy that keeps each user to their parental limit, single sign-on, and optional request and issue notifications. The step-by-step guide is [Seerr Setup](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Setup).
- **Push notifications** that reach Moonfin apps even when they are closed, covering Seerr activity, new media added to your library, and admin broadcasts. See [Seerr Notifications](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Notifications).
- **Admin tools** for setting server-wide defaults across every client setting, hiding libraries and adding custom list rows for new users, pushing defaults to existing users, and broadcasting a message to everyone at once.
- **Sign-in PINs kept on the server**, so a user's PIN follows them to every device. Wrong guesses are slowed down, and admins can set or reset anyone's PIN.
- **Live TV recording checks** that mark recordings which missed part of their airing as Incomplete, and can record the next full showing for you.
- **Settings backup and restore** (Jellyfin): Moonbase keeps a spare copy of your server-wide settings and offers to put it back if they ever come back reset.
- **App reports on Emby**, so the apps' Send report to server button works there too. Jellyfin has this built in.
- **Server messages**: an admin can write a note (title, Markdown body, color, audience, start and end dates, an optional link button) that shows up in people's apps, even if they were offline when it was sent.
- **Custom themes** with a built-in editor, plus server-side upload and validation.
- **Retro games** support for browsing and playing game libraries, from folders or a RomM server, with saves synced per user. Each user can link their own RomM account so their saves sync with RomM too. On Jellyfin, an optional switch lets PSP games start in the web app. See [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games).
- **Custom rows** built from Letterboxd, TMDB, MDBList and IMDb lists, cached on the server and filtered by each user's parental limit.
- **An active downloads dashboard** with live transcode metrics in the admin panel, plus a per-client view so a transcoded download can show its own progress and ETA.
- **Personal ratings served back to clients**, so a library can be sorted by your own rating and filtered by liked or disliked, which neither server can do on its own.
- **Audiobook bookmarks and notes** stored on the server and synced between clients.

## Configuration

Open your server dashboard, go to Plugins, and select **Moonbase**. The [Admin Guide](https://github.com/Moonfin-Client/Plugin/wiki/Admin-Guide) walks through every tab. The main things to set up:

- Your Seerr URL and whether Seerr is enabled
- Shared MDBList and TMDB API keys, so individual users don't need their own. The TMDB key also feeds the seasonal row and the rating checks on custom rows
- Whether Moonbase Sync is on (required for the Seerr and ratings integrations)
- Anime markers, and on Jellyfin whether Moonfin Recommends handles similar items
- Incomplete recording detection and re-recording on the Live TV tab
- Users' sign-in PINs on the Accounts tab
- Default user settings that new users inherit, with a button to push them to existing users
- Server messages and the quick broadcast
- Web startup options and custom theme uploads

Users change their own preferences from the in-app settings page in any Moonfin client. Settings are stored per user and shared across clients, with an optional per-device override for desktop, mobile, and TV.

<details>
<summary><b>Advanced:</b> the User defaults tab</summary>

The User defaults tab covers every client setting, in the same sections the apps use: Account, General, Appearance, Home Screen, Libraries, Details Page, Playback and Integrations. Each control mirrors what the clients actually offer, so a default set here is one a client can honor. A setting left on Not set (user decides) passes the user's own choice through.

</details>

## Documentation

The deeper reference material lives in the [Wiki](https://github.com/Moonfin-Client/Plugin/wiki):

| Page | What it covers |
|------|----------------|
| [Installation](https://github.com/Moonfin-Client/Plugin/wiki/Installation) | The Jellyfin catalog route, the Emby zip, the first things to switch on, updating and uninstalling |
| [Admin Guide](https://github.com/Moonfin-Client/Plugin/wiki/Admin-Guide) | The plugin page tab by tab, the scheduled tasks, and what differs on Emby |
| [Common Problems](https://github.com/Moonfin-Client/Plugin/wiki/Common-Problems) | Plain fixes for catalog, web app, header button, sync, reset settings, Seerr, ratings, rows, the seasonal row, anime markers and games trouble |
| [API Reference](https://github.com/Moonfin-Client/Plugin/wiki/API-Reference) | Every plugin endpoint, with methods, auth, the Seerr config response, and the server messages endpoints |
| [Settings Sync](https://github.com/Moonfin-Client/Plugin/wiki/Settings-Sync) | How sync works, the settings envelope, and the full list of synced settings |
| [Data Locations](https://github.com/Moonfin-Client/Plugin/wiki/Data-Locations) | Where the plugin stores user settings, themes, saves, caches, and the settings backup on the server |
| [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games) | Game libraries, cores, ROMs, BIOS, saves, and in-browser play |
| [Seerr Setup](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Setup) | The plain step-by-step guide to connecting Seerr |
| [Seerr Notifications](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Notifications) | Push delivery, webhooks, notification types, and self-hosted relays |
| [Themes](https://github.com/Moonfin-Client/Plugin/wiki/Themes) | The theme editor and custom theme uploads |
| [Reverse Proxy and Seerr](https://github.com/Moonfin-Client/Plugin/wiki/Reverse-Proxy-and-Seerr) | Path forwarding and Seerr single sign-on behind a proxy |
| [Building from Source](https://github.com/Moonfin-Client/Plugin/wiki/Building-from-Source) | Building the Jellyfin and Emby plugins from this repo |

## Contributing

Contributions are welcome. Check the existing issues first, open an issue before starting a large change, match the existing code style, and test your changes on desktop and mobile. Features that would help all Jellyfin or Emby users are worth proposing upstream first.

To submit a change, fork the repo, create a feature branch, make your changes with clear commit messages, and open a pull request with a clear description. [CONTRIBUTING.md](CONTRIBUTING.md) has the full guidelines.

## Support and Community

- **Issues** for bugs and feature requests: [GitHub Issues](https://github.com/Moonfin-Client/Plugin/issues)
- **Discussions** for questions and ideas: [GitHub Discussions](https://github.com/Moonfin-Client/Plugin/discussions)

## Credits

Moonfin is built on the work of others:

- **[Jellyfin Project](https://jellyfin.org)** for the foundation and upstream codebase
- **[Druidblack](https://github.com/Druidblack)** for the original MDBList Ratings plugin
- **Moonfin Contributors** for everything they have added to the project

## License

This project is licensed under GPL-3.0. See the [LICENSE](LICENSE) file for details.

---

<p align="center">
   <strong>Moonfin</strong> is an independent project and is not affiliated with the Jellyfin or Emby projects.<br>
   <a href="https://github.com/Moonfin-Client">Back to the main Moonfin project</a>
</p>
