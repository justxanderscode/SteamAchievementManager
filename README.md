# Steam Achievement Manager

Steam Achievement Manager (SAM) is a lightweight, portable application used to manage achievements and statistics in the popular PC gaming platform Steam. This application requires the [Steam client](https://store.steampowered.com/about/), a Steam account and network access. Steam must be running and the user must be logged in.

This is the code for SAM. The closed-source version originally released in 2008, last major release in 2011, and last updated in 2013 (a hotfix).

The code is being made available so that those interested can do as they like with it.

There are some changes to the code since the last closed-source release:
- General code maintenance to bring it into a more modern state.
- Icons have been replaced with ones from the Fugue Icons set.
- Version has been bumped to 7.0.x.x to indicate the open-source release.

[Download latest release](https://github.com/gibbed/SteamAchievementManager/releases/latest).

[![Build status](https://ci.appveyor.com/api/projects/status/00vic6jliar6j0ol/branch/master?svg=true)](https://ci.appveyor.com/project/gibbed/steamachievementmanager/branch/master)

## WinUI 3 version (SAM.App)

`SAM.App` is an optional, modern Windows 11 front end for the same Steam code. It sits next to the
classic WinForms tools and does not replace them: `SAM.sln` (SAM.API, SAM.Picker, SAM.Game) is
unchanged and builds as before.

- **One program, same model:** `SAM.exe` without arguments opens the library, `SAM.exe <app id>`
  opens a game in its own process, like SAM.Picker and SAM.Game.
- **Library:** tiles with capsule images, search, filter by type, sort by name or app ID, open by
  app ID, context menu (store page, show in Steam, copy app ID). The game list and images are cached
  in `%LOCALAPPDATA%\SteamAchievementManager`, with the last copy of the game list as fallback when
  gib.me is unreachable.
- **Game window:** unlock, lock and edit statistics as before, with a header image, progress, search
  and filters. Pending changes are marked and counted, a summary is shown before saving, protected
  achievements are skipped by the bulk actions, and closing or reloading warns about unsaved changes.
  Resetting is a single dialog with an explicit confirmation.
- **Look:** Mica, custom title bar, light and dark theme following Windows. Keyboard: `Ctrl+F` search,
  `F5` reload, `Ctrl+S` save.
- **Languages:** 47 translations covering 72 language/country variants, right-to-left layout for
  Arabic and Hebrew, picked from Windows or chosen in the library. Texts live in
  `SAM.App/Strings/*.json`; `python tools/strings.py` checks every file against `en.json` and
  regenerates `Localization/Strings.g.cs`.
- **Achievement lists:** sorts every owned game into the categories of the
  [Steam Achievement Statistics](https://steamcommunity.com/sharedfiles/filedetails/?id=3128095897)
  collections (`1.0.0 All` … `1.2.4 Can't Finish`, split by whether a game counts on the profile or is
  Profile Features Limited) and exports them as Steam guide text, split to stay below the section
  limit. The data comes from Steam's own library cache
  (`userdata\<account>\config\librarycache\achievement_progress.json`), so no game is launched and
  nothing is written to Steam.

It is a 64-bit app (.NET 10, Windows App SDK 1.8, unpackaged and self-contained). `SAM.API.x64`
compiles the same `SAM.API` sources for x64, and the schema reader and stat definitions are
linked from `SAM.Game` rather than copied. Building needs the .NET 10 SDK only:

```
dotnet build SAM.WinUI.sln -c Release -p:Platform=x64
dotnet publish SAM.App/SAM.App.csproj -c Release -r win-x64 -p:Platform=x64 -o upload/SAM.App
```

Requires Windows 10 version 2004 or later. Debug builds understand `--demo <app id>` (game window
with the real schema from Steam's cache and simulated states, without talking to Steam) and
`--language <locale>` for checking translations.

## Attribution

Most (if not all) icons are from the [Fugue Icons](https://p.yusukekamiyamane.com/) set.
