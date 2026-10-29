# NekoDex

## 🔮 Multi-Source Roadmap

NekoDex interacts with each platform via its own dedicated provider. The catalog below represents the current status: active, paused, or pending.

### ⚡ REST / JSON APIs (nhentai-Style)
- `nhentai` *(Active)*
- `HentaiFox` | `HentaiHand` | `HentaiEra` | `HentaiNexus` *(Active)*
- `3Hentai` | `Asmhentai` *(Active)*

### 🏷️ GData & Namespace Parsing (E-Hentai-Style)
- `E-Hentai` *(Active, Direct Search, `next=` offset)*
- `ExHentai` *(Paused, Cookie session available, hidden from search selection)*
- `LoFi E-Hentai` | `HentaiDB` | `Doujins.com` *(Active)*

### 📦 Static CDN & Binary Index (Hitomi-Style)
- `Hitomi.la` *(Active, 30 IDs per page)*
- `SimplyHentai` *(Active)*
- `Kemono` *(Active, `kemono.cr`)*
- `MangaDex` *(Planned / Prepared)*

### 🔧 Custom / Direct Scrapers
- `HentaiEnvy` | `HentaiClap` | `HentaiRun` *(Active, custom parsers)*

### 🗑️ Removed
- `HBrowse` | `Hentai2Read` | `DoujinMoe` | `HentaiRead`

---

## 🧹 Cleanup & Source Architecture

Dead and unstable sources have been removed from the enum, catalog, and web search. Associated entries in `online_site_tags` are purged upon database initialization (`hentai2read`, `doujinmoe`, `hbrowse`, `hentairead`).

- `HentaiEnvoy` has been consistently renamed to `HentaiEnvy`. Legacy names (`hentaienvoy`) resolve to `hentaienvy.com` via `SourceCatalog.FromName`.
- `HentaiRun` parses the Next.js catalog at `https://hentairun.com/`. Single tags route to `/tag/{slug}/` and `/tag/{slug}/page/2/`, while multiple tags route to `/search/?q=`. Cards point to `/gallery/{id}/`, and covers load from `m*.hentairun.com`.
- `HentaiClap` parses the PHP catalog at `https://hentaiclap.com/`. Search queries run via `/search/?key=` and `&page=`. Single tag paths like `/tag/anal/` return 404; the tag index resides under `/tags/popular/?page=`. Cards utilize `data-src` pointing to `m*.hentaiclap.com`, titles inside `h2`, and galleries under `/gallery/{id}/`.
- `HentaiEnvy` parses its custom theme at `https://hentaienvy.com/`. Single tags map to `/{segment}/{slug}/?page=`, and multi-tag searches map to `/search/?key=`. Cards match `hnv-gallery-card__cover`, covers load from `m*.hentaienvy.com`, and galleries reside at `/gallery/{id}/`. The tag list `/tags/popular/` provides names and badge counts.
- All three custom parsers transmit proper browser headers and page referrers via `SiteHttp`. HTML-based catalogs (`BrowserListingProvider`, along with `Asmhentai` and `SimplyHentai`) inherit stored session cookies and gallery-dl's Netscape cookie file. If `cf_clearance` is present, subsequent requests match gallery-dl's User-Agent.
- `LoFi E-Hentai`, `HentaiDB`, and `Doujins.com` rely on `EHentaiStyleProvider` and execute searches within their own domains. LoFi pages through `next={gallery-id}` and passes the target address `https://e-hentai.org/g/{id}/{token}/` to gallery-dl. HentaiDB filters its embedded catalog (`tag:`, `studio:`) and slices 30 entries per page.
