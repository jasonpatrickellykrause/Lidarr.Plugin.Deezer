# Deezer for Lidarr
This plugin provides a Deezer indexer and downloader client for Lidarr using direct communication rather than using Deemix as a middleman.

### ⚠️ WARNING: Deezer seems to be cracking down on downloading tools and this wasn't designed super well for that, I've made minor changes to try to improve it, but there's still no guarantee you won't be limited or banned. ⚠️

## About this fork
This is a fork of [TrevTV/Lidarr.Plugin.Deezer](https://github.com/TrevTV/Lidarr.Plugin.Deezer). The upstream plugin's last release was 10.1.0.18 in November 2025, and its maintainer isn't merging pull requests. This fork fixes problems that cost users their ARLs or left them with failed downloads, and protects the ARL in Lidarr's interface.

The main differences from upstream:
- **ARL protection:** Lidarr masks the ARL in its interface and API, and a Saved ARL field shows only its last four characters.
- **Gentler on your account:** the plugin paces track downloads and search lookups, stops an album as soon as Deezer rejects the ARL, and no longer retries a dead session without limit.
- **Fewer failed grabs:** search results only offer FLAC or MP3 320 when every track on the album is available at that quality.
- **MusicBrainz tagging:** downloaded files carry the release, artist, and recording IDs Lidarr matched, which helps imports.
- **Maintained dependencies:** the plugin builds [a maintained fork of DeezNET](https://github.com/jasonpatrickellykrause/DeezNET) from source, and Dependabot, CodeQL, and signed build provenance cover both projects.

You can't install this fork alongside TrevTV's plugin. See [Switching from another Deezer plugin](#switching-from-another-deezer-plugin).

## Installation
This requires your Lidarr setup to be using the `plugins` branch. My docker-compose is setup like the following.
```yml
  lidarr:
    image: ghcr.io/hotio/lidarr:pr-plugins
    container_name: lidarr
    environment:
      - PUID:100
      - PGID:1001
      - TZ:Etc/UTC
    volumes:
      - /path/to/config/:/config
      - /path/to/downloads/:/downloads
      - /path/to/music:/music
    ports:
      - 8686:8686
    restart: unless-stopped
```

1. In Lidarr, go to `System -> Plugins`, paste `https://github.com/jasonpatrickellykrause/Lidarr.Plugin.Deezer` into the GitHub URL box, and press Install.
2. Go into the Indexer settings and press Add. In the modal, choose `Deezer` (under Other at the bottom).
3. Paste your Deezer ARL into the ARL box and press Save. It will load for a while as it makes several calls to Deezer. After saving, the ARL is masked, and the Saved ARL field shows its last four characters so you can compare it with a new one.
4. Go into the Download Client settings and press Add. In the modal, choose `Deezer` (under Other at the bottom).
5. Put the path you want to download tracks to and fill out the other settings to your choosing.
   - If you want `.lrc` files to be saved, go into the Media Management settings and enable Import Extra Files and add `lrc` to the list.
6. Go into the Profile settings and find the Delay Profiles. On each (by default there is only one), click the wrench on the right and toggle Deezer on.
7. Optional: To prevent Lidarr from downloading all track files into the base artist folder rather than into their own separate album folder, go into the Media Management settings and enable Rename Tracks. You can change the formats to your liking, but it helps to let each album have their own folder.

## Switching from another Deezer plugin
Only one copy of a Deezer plugin can be installed. Two copies (for example TrevTV's plugin and this fork) define the same indexer and download client, and Lidarr then fails with `Sequence contains more than one matching element` in the logs. This plugin shows a health check error on the System > Status page when it finds more than one copy.

To switch from TrevTV's plugin:
1. Back up Lidarr (System > Backup > Backup Now).
2. In System > Plugins, uninstall the TrevTV Deezer plugin, then restart Lidarr.
3. Install this plugin from `https://github.com/jasonpatrickellykrause/Lidarr.Plugin.Deezer`, then restart Lidarr again.

Your Deezer indexer and download client settings are kept in Lidarr's database, so you don't need to add them again.

If you installed this fork's 10.1.0.1 release, the uninstall button can't remove it, because that build reports TrevTV as its owner. Remove its folder manually instead:
1. Stop Lidarr.
2. Delete the `Lidarr.Plugin.Deezer` folder under `plugins/jasonpatrickellykrause/` in Lidarr's data folder. That's `/var/lib/lidarr/plugins` for a native Linux install, or `/config/plugins` in Docker.
3. Start Lidarr and install the current release.

## Changelog
Each merge to `main` that changes the plugin publishes a release. Merges that only change documentation or CI configuration don't. Every release also lists its commits on the [Releases page](https://github.com/jasonpatrickellykrause/Lidarr.Plugin.Deezer/releases).

### 10.2.0.24
- Fixed a `MissingMethodException` in the duplicate install health check on Lidarr 3.1.5 and later. The check failed every time Lidarr ran its health checks.
- Releases target `main` again, so **System** > **Plugins** offers them as updates. Lidarr skipped 10.2.0.15 and 10.2.0.19 until their releases targeted `main`.

### 10.2.0.19
- **Hide Albums With Missing Tracks** now also catches tracks that Deezer lists but won't serve, which show as greyed out in the Deezer app. Before, Lidarr could grab an album with such a track, and the download failed on that track with error 2002.
- Merges that only change documentation or CI configuration no longer publish a release.

### 10.2.0.15
- Each release tag now points at the commit CI built, and releases publish one at a time so each changelog is complete. No change to the plugin itself.

### 10.2.0.13
- After a search, the plugin looks up each album at most two at a time with a short pause, instead of sending a whole page of lookups to Deezer at once.

### 10.2.0.12
- The indexer fields read **ARL** and **Saved ARL**, and Saved ARL sits directly below ARL.

### 10.2.0.9
- The plugin reports the repository CI built it from, so updating and uninstalling from **System** > **Plugins** use the right folder. Forks get this automatically.
- A health check reports an error on **System** > **Status** when it finds more than one installed copy of the plugin, and lists each folder.

### 10.2.0.7
- Lidarr masks the ARL in its interface and API. A new Saved ARL field shows its last four characters.
- Removed the code that downloaded shared ARLs from a public web page.
- Removed unused packages from the plugin: AngleSharp, AngleSharp.XPath, and a SkiaSharp preview build.
- Builds fail on high or critical NuGet advisories. CI runs DeezNET's decryption tests, pins its actions to commit SHAs, and attests build provenance. Verify a release with `gh attestation verify Lidarr.Plugin.Deezer.net8.0.zip --repo jasonpatrickellykrause/Lidarr.Plugin.Deezer`.
- Dependabot updates the DeezNET submodule.

### 10.2.0.5
First release of this fork. Compared with TrevTV's 10.1.0.18:

**ARL and account safety**
- A rejected ARL now fails the validity check. Before, the check passed for any ARL.
- The plugin checks the ARL before downloading, and stops requesting an album's remaining tracks once Deezer rejects the ARL.
- Downloads pause between tracks (**Download Delay**, 1.5 seconds by default, with random extra time), and the indexer waits 1 second between search requests.

**Downloads**
- Search results only offer FLAC or MP3 320 when every available track on the album has a file at that quality. Before, the plugin offered FLAC for albums Deezer can't serve in FLAC, and every track failed with `NoSourcesAvailableException`.
- New advanced download client option, **Fall Back to Lower Quality** (off by default), downloads the next lower quality when a track isn't available at the grabbed one.
- Includes DeezNET 1.2.3. It fixes stale bytes at the end of decrypted tracks and limits session retries. See the [DeezNET changelog](https://github.com/jasonpatrickellykrause/DeezNET#changelog).

**Tagging**
- Downloaded files get MusicBrainz release, release group, artist, and recording IDs from the album Lidarr matched. Based on [TrevTV/Lidarr.Plugin.Deezer#27](https://github.com/TrevTV/Lidarr.Plugin.Deezer/pull/27) by jtstothard, with recording IDs matched by track position so multi-disc albums tag correctly.

**Releases**
- Versions start at 10.2.0 so they sort above TrevTV's 10.1.0.x, and releases publish automatically instead of as drafts.

## Licensing
All of these libraries have been merged into the final plugin assembly due to (what I believe is) a bug in Lidarr's plugin system.
- [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) is licensed under the MIT license. See [LICENSE](https://github.com/JamesNK/Newtonsoft.Json/blob/master/LICENSE.md) for the full license.
- [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) is licensed under the MIT license. See [LICENSE](https://github.com/bcgit/bc-csharp/blob/master/LICENSE.md) for the full license.
- [TagLibSharp](https://github.com/mono/taglib-sharp) is licensed under the LGPL-2.1 license. See [COPYING](https://github.com/mono/taglib-sharp/blob/main/COPYING) for the full license.
- [DeezNET](https://github.com/jasonpatrickellykrause/DeezNET) (a fork of [TrevTV/DeezNET](https://github.com/TrevTV/DeezNET)) is licensed under the GPL-3.0 license. See [LICENSE](https://github.com/jasonpatrickellykrause/DeezNET/blob/main/LICENSE) for the full license.
