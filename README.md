## 🔮 Multi-Source Roadmap

NekoDex spricht jede Plattform über einen eigenen Provider an. Der Katalog unten ist der aktuelle Stand: angebunden, pausiert oder noch offen.

### ⚡ REST / JSON APIs (nhentai-Style)
* `nhentai` *(Aktiv)*
* `HentaiFox` | `HentaiHand` | `HentaiEra` | `HentaiNexus` *(Aktiv)*
* `3Hentai` | `Asmhentai` *(Aktiv)*

### 🏷️ GData & Namespace Parsing (E-Hentai-Style)
* `E-Hentai` *(Aktiv, Direct Search, `next=`-Offset)*
* `ExHentai` *(Pausiert, Cookie-Session vorhanden, in der Suche nicht wählbar)*
* `LoFi E-Hentai` | `HentaiDB` | `Doujins.com` *(Aktiv)*

### 📦 Static CDN & Binary Index (Hitomi-Style)
* `Hitomi.la` *(Aktiv, 30 IDs pro Seite)*
* `SimplyHentai` *(Aktiv)*
* `Kemono` *(Aktiv, `kemono.cr`)*
* `MangaDex` *(Vorbereitet)*

### 🔧 Custom / Direct Scraper
* `HentaiEnvy` | `HentaiClap` | `HentaiRun` *(Aktiv, eigene Parser)*

### 🗑️ Entfernt
* `HBrowse` | `Hentai2Read` | `DoujinMoe` | `HentaiRead`

## 🧹 Bereinigung & Quellen-Architektur

Tote und instabile Quellen sind aus Enum, Katalog und Websuche entfernt. Zugehörige Zeilen in `online_site_tags` werden beim Öffnen der Datenbank gelöscht (`hentai2read`, `doujinmoe`, `hbrowse`, `hentairead`).

* `HentaiEnvoy` heißt durchgängig `HentaiEnvy`. Alte Namen (`hentaienvoy`) landen über `SourceCatalog.FromName` auf `hentaienvy.com`.
* `HentaiRun` liest den Next.js-Katalog auf `https://hentairun.com/`. Ein Tag geht nach `/tag/{slug}/` und `/tag/{slug}/page/2/`, mehrere Tags nach `/search/?q=`. Karten zeigen auf `/gallery/{id}/`, Cover auf `m*.hentairun.com`.
* `HentaiClap` liest den PHP-Katalog auf `https://hentaiclap.com/`. Die Suche läuft über `/search/?key=` und `&page=`. Einzelne Tag-Pfade wie `/tag/anal/` liefern 404; der Index liegt unter `/tags/popular/?page=`. Karten nutzen `data-src` auf `m*.hentaiclap.com`, den Titel in `h2` und die Galerie `/gallery/{id}/`.
* `HentaiEnvy` liest das eigene Theme auf `https://hentaienvy.com/`. Ein Tag geht nach `/{segment}/{slug}/?page=`, mehrere Tags nach `/search/?key=`. Karten sind `hnv-gallery-card__cover`, Cover liegen auf `m*.hentaienvy.com`, Galerien unter `/gallery/{id}/`. Die Tagliste `/tags/popular/` liefert Name und Badge-Zahl.
* Die drei Custom-Parser senden Browser-Header und den Seiten-Referer über `SiteHttp`. HTML-Kataloge (`BrowserListingProvider`, dazu `Asmhentai` und `SimplyHentai`) übernehmen gespeicherte Sitzungs-Cookies und die Netscape-Datei von gallery-dl. Liegt `cf_clearance`, folgen die nächsten Abrufe dem User-Agent von gallery-dl.
* `LoFi E-Hentai`, `HentaiDB` und `Doujins.com` hängen an `EHentaiStyleProvider` und suchen auf der eigenen Domain. LoFi blättert mit `next={Galerie-ID}` und schreibt gallery-dl die Adresse `https://e-hentai.org/g/{id}/{token}/`. HentaiDB filtert den eingebetteten Katalog (`tag:`, `studio:`) und schneidet 30 Zeilen pro Seite. Doujins.com sucht über `/searches?words=`.
* Registrierte Provider mit `ITagCatalogSource` (und HentaiFox) laufen als Direct Search: die Seite kommt aus `SearchAsync`, nicht aus dem allgemeinen API-Strom.

## ⏩ Paginierung & Performance

Endlos-Scroll und „Mehr laden“ sind aus der Websuche entfernt. Jede Online-Quelle liefert einen festen Block.

* `WebPageLimit` ist 30. Ein Wechsel ersetzt das Raster (`append: false`). Die Leiste zeigt `← Zurück`, die Seitenzahl und `Weiter →`.
* Seite 1 erscheint, sobald ihr Block da ist. Dieselbe Seite liegt danach im Seitencache und kommt ohne Ladeoverlay zurück. Ein stilles Vorladen der noch nicht geöffneten Folgeseite ist nicht angebunden.
* E-Hentai ignoriert `page=`. Die nächste Portion steht in `next=` (Galerie-ID) und wird pro Seite gemerkt.
* HentaiNexus sucht Seite 1 als `/?q=`. Ab Seite 2 lautet der Pfad `/page/{n}?q=`.
* Hitomi schneidet den ID-Index in 30er-Blöcke: Seite 1 ist Index 0–29, jede weitere Seite die nächsten 30 IDs.

## 🎛️ HUD & Auto-Collapse Web Search

* Mit dem Start einer Suche fährt die obere Web-Search-Leiste ein (`MaxHeight` mit `QuadraticEase`). Der Mauszeiger am oberen Rand klappt sie wieder auf, ein schmaler Griff markiert den eingeklappten Zustand. Solange die Tag-Vorschläge offen sind, bleibt die Leiste offen.
* Die Quellenwahl der Bibliothek sitzt als Popout direkt rechts neben der einen Suchleiste. Die frühere Quellen-Zeile darunter ist weg.
* Der eigene Navigationspunkt „Favoriten“ ist entfernt. Lesestatus, Kategorien und Mehrfachauswahl liegen in der Bibliothek.

## 📚 Bibliothek

Die Bibliothek nutzt eine Zeile (`LibraryBar`) und das Cover-Raster darunter.

* Status-Chips: `Alle`, `Ungelesen`, `Am Lesen`, `Abgeschlossen`.
* Kategorien: `+` legt an, das Kontextmenü eines Chips benennt um oder löscht. Fällt der aktive Filter weg, wird er mitgezogen oder geleert.
* Seitenzahl: zwei Felder `von` / `bis` filtern `page_count` (`MinPages`, `MaxPages`). Auf der Karte steht die Seitenzahl neben dem Titel.
* Mehrfachauswahl: `Alle`, `Aufheben`, `Ausgewählte löschen`, `Kategorie`.
* Rechtsklick auf eine Karte: Lesestatus, Kategorie zuweisen (bestehende oder neue), `Löschen`.
