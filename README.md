# NekoDex (v1.0.0 - Early Access / In Development)

> **Doujinshi Reader, Downloader and Archive in One**

**NekoDex** is a high-performance C# WPF desktop application featuring a modern, minimalist dark-mode HUD aesthetic. It serves as a unified discovery engine, local archive, and downloader for manga and doujinshi across multiple supported web sources.

---

## ⚠️ Current Status: v1.0.0 (Work in Progress)

This repository represents the initial **v1.0.0 release in active development**. Key foundational features, UI frameworks, and primary scrapers are functional, but active development is ongoing.

### 🗺️ Future Roadmap & Upcoming Enhancements
- **Expanded Source Matrix:** Integration of additional manga and doujinshi sites to broaden search range and archival coverage.
- **Enhanced Local Archiving:** Automated indexing improvements, custom tag creation, and bulk meta-data updates.
- **Deep Cloudflare & Security Bypasses:** Continuous optimization of header spoofing and dynamic index fetching.
- **Performance Tweaks:** Further UI acceleration for large library grids and asynchronous image rendering.

---

## ✨ Features

- **Unified HUD Interface:** Minimalist dark cyber/HUD theme optimized for fast browsing.
- **Multi-Source Scraping:** Direct support for `nhentai`, `Hitomi.la`, and `E-Hentai`.
- **Full Namespace Tagging:** Preserves rich metadata (e.g., `female:`, `artist:`, `language:`, `parody:`) for accurate multi-tag searches.
- **Binary Indexing (Hitomi.la):** Uses local binary Protobuf/Nozomi index processing to fast-track query results and bypass aggressive site rate limits.
- **`gallery-dl` Integration:** Built-in engine fallback for maximum scraping reliability and seamless image downloading.
- **Flexible UI Layout:** Dynamic collapsible tag catalog with space-efficient compact pagination controls.
- **Local SQLite Persistence:** Fast local metadata caching and collection management.

---

## 🛠️ How It Works (Architecture)

NekoDex combines site-specific API fetching with local binary indexing and custom web scrapers:

1. **Query Parsing & Formatting:** Search queries are processed with site-specific operators (e.g., `female:"big breasts"`, `language:english`) and URL-encoded.
2. **Metadata Fetching:**
   - **E-Hentai:** Utilizes `gdata` JSON-RPC API queries to parse full `namespace:tag` metadata.
   - **Hitomi.la:** Queries binary Protobuf/Nozomi index files locally/remotely to skip heavy browser emulation.
   - **nhentai:** Resolves standard REST JSON endpoints with full Referer/Header spoofing.
3. **Local Caching:** Parsed tags and gallery entries are stored in a local SQLite database (`online_site_tags`) to allow instant auto-completion and offline filtering.
4. **Fallback Engine:** Complex media downloads and edge-case scraping fall back on an integrated `gallery-dl` instance.

---

## 🚀 Getting Started & Usage

### Prerequisites
- **OS:** Windows 10 / 11 (64-bit)
- **Runtime:** [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) *(or standalone portable bundle)*

### How to Run
1. Download the latest `NekoDex.exe` release from the [Releases](https://github.com/User3215432/NekoDex/releases) section.
2. Run `NekoDex.exe` (no installation required).

### Basic Usage Guide

1. **Selecting Sources:** Choose your active source platform (`nhentai`, `Hitomi.la`, or `E-Hentai`) from the top bar.
2. **Tag Filtering & Expansion:**
   - Click the **Tags** toggle to open the full tag catalog overlay for granular category filtering.
   - Collapse the tag panel after submitting a search to maximize the gallery grid view.
3. **Searching:** Enter keywords or namespace tags into the search HUD bar and press `Enter` or click **Search**.
4. **Browsing & Reading:** Click any cover tile to view details, inspect full tag lists, or launch the internal reader/downloader.
5. **Pagination:** Use the compact transparent navigation bar at the bottom to jump between result pages.

---


1. Clone the repository:
   ```bash
   git clone [https://github.com/User3215432/NekoDex.git](https://github.com/User3215432/NekoDex.git)
