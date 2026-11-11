# Gestione File — Analisi di fattibilità e piano di sviluppo

## 1. Il problema
Nel tempo si accumulano in Download, Desktop e dischi esterni centinaia di
`setup.exe`, `.msi`, pacchetti driver, zip e ISO. Problemi tipici:
- nomi poco parlanti (`setup (3).exe`, `x64_en.exe`)
- stesse app scaricate più volte, in versioni diverse
- driver di cui non si sa più a quale periferica appartengono
- non si sa cosa è già installato e cosa si può buttare

## 2. Fattibilità (sintesi: **alta**)

| Funzionalità | Fattibilità | Come |
|---|---|---|
| Scansione cartelle scelte | Alta | Enumerazione ricorsiva + `FileSystemWatcher` per gli aggiornamenti |
| Ricerca istantanea | Alta | SQLite FTS5 (prefissi, trigram), indice in memoria per i filtri |
| Categoria / tipo | Alta | Estensione + "magic bytes" + metadati PE/MSI + regole |
| Versione e produttore | Alta | Risorse VERSIONINFO dei PE (`FileVersionInfo`), tabella `Property` degli MSI (ProductName, ProductVersion, Manufacturer) |
| Firma digitale | Alta | Authenticode (`WinVerifyTrust`) → editore verificato |
| Duplicati esatti | Alta | Pipeline dimensione → hash parziale (primi/ultimi 64 KB) → hash completo (xxHash3/BLAKE3) |
| "Stesso prodotto, versioni diverse" | Media-Alta | Raggruppamento per ProductName+Manufacturer normalizzati, ordinamento per versione |
| Driver | Media-Alta | Parsing dei file `.inf` (Class, Provider, DriverVer, HardwareID) e confronto con le periferiche presenti (SetupAPI / `pnputil /enum-drivers`) |
| Installato vs non installato | Alta | Chiavi registro `Uninstall` (HKLM, HKLM\WOW6432Node, HKCU) + app MSIX |
| Contenuto di zip/ISO | Media | Lettura indice archivi (zip nativo, 7z/rar via SharpCompress), ISO montabili |
| Ricerca su tutto il disco tipo "Everything" | Media | Lettura della MFT/USN Journal NTFS: velocissima ma richiede privilegi admin. Fase successiva, non necessaria per l'MVP |
| Controllo aggiornamenti online | Media-Bassa | Possibile via `winget` (manifest pubblici) per le app note; non affidabile per i driver |

Rischi principali:
- **Installer "bootstrapper"** (es. scaricatori web) con metadati poveri → servono euristiche sul nome file e sulla firma.
- **Prestazioni dell'hashing** su dischi lenti/USB → hash solo su richiesta o in background a bassa priorità.
- **Spostare file** è l'operazione più delicata: per default l'app *non tocca* i file, crea una vista organizzata; l'eventuale riorganizzazione fisica è opzionale, con anteprima e annullamento (log delle operazioni).

## 3. Scelta del linguaggio

| Opzione | Pro | Contro | Verdetto |
|---|---|---|---|
| **C# / .NET 10 + WPF** | Accesso diretto alle API Windows (PE, MSI, Authenticode, SetupAPI, registro, USN), ottime prestazioni, UI desktop matura, un solo linguaggio, distribuzione semplice | Solo Windows (ma è il requisito) | **Consigliato** |
| C# + WinUI 3 | UI più moderna | Ecosistema ancora meno stabile di WPF, packaging più rigido | Alternativa valida |
| Rust + Tauri | Prestazioni massime, eseguibile leggero | Due linguaggi (Rust + web), interop Windows più laborioso, sviluppo più lento | Solo se si punta alla massima leggerezza |
| Python + Qt | Prototipazione rapida | Lento su grandi indici, distribuzione scomoda, UI meno nativa | Sconsigliato per il prodotto finale |
| Electron | UI web flessibile | Pesante (≈150 MB+ RAM), API Windows tramite moduli nativi | Sconsigliato |

**Raccomandazione: C# su .NET 10 (LTS) con WPF e tema Fluent, SQLite (Microsoft.Data.Sqlite) con FTS5.**
L'app vive interamente nel mondo Windows: .NET ha in libreria o tramite P/Invoke
tutto ciò che serve, e WPF con MVVM (CommunityToolkit.Mvvm) è lo stack desktop
più produttivo e stabile oggi.

## 4. Architettura

```
PcOrg.sln
├─ src/
│  ├─ PcOrg.Core        # classificazione, scansione, indice SQLite/FTS5, spostamenti (nessuna dipendenza da WPF)
│  │  ├─ Classification/   categorie e regole
│  │  ├─ Indexing/         enumerazione veloce delle cartelle
│  │  ├─ Storage/          database, ricerca, log degli spostamenti
│  │  └─ Moving/           piano di spostamento (anteprima) ed esecuzione/annullamento
│  └─ PcOrg.App         # interfaccia WPF (MVVM con CommunityToolkit.Mvvm), produce GestioneFile.exe
└─ tests/
   └─ PcOrg.Core.Tests  # xUnit, girano anche su Linux
```

Gli estrattori dei metadati sono in `PcOrg.Core/Metadata`. La lettura del formato PE (versione,
architettura, firma, sezioni) è scritta da zero e funziona su ogni sistema; MSI (`msi.dll`) e verifica
della firma (`WinVerifyTrust`) usano API di Windows e fuori da Windows vengono saltate.
`tests/PcOrg.App.Tests` apre le finestre WPF durante la build su Windows per intercettare errori nello XAML.

Flusso: **Cartelle monitorate → Scanner → Estrattori metadati (in background) → DB → UI**.

Modello dati (fase 1 in grassetto):
- **`roots`** (cartelle monitorate)
- **`files`** + **`files_fts`** (percorso, nome, cartella, dimensione, data, estensione, categoria; hash dalla fase 3)
- **`file_meta`** (prodotto, produttore, descrizione, versioni, architettura, firma, tipo di installer, chiave prodotto)
- **`move_batches`** / **`move_log`** (cronologia spostamenti annullabili)
- `DriverInfo` (classe, provider, versione, data, hardware ID, periferica presente sì/no)
- `InstalledApp` (dal registro, per l'incrocio "già installato")
- `Tag` (manuali, liberi)

Categorie iniziali (configurabili da regole JSON):
Installer (EXE/MSI/MSIX), Driver, App portabili, Archivi, Immagini disco (ISO/IMG/VHD),
Documenti, Media, Script, Altro.

## 5. Interfaccia (MVP)
- Barra di ricerca sempre in alto, risultati mentre si scrive; filtri rapidi per categoria, produttore, "installato/non installato", "duplicato".
- Pannello laterale categorie con conteggi e spazio occupato.
- Dettaglio file: versione, produttore, firma, hash, percorsi dei duplicati, azioni (apri cartella, esegui, copia percorso, aggiungi tag, sposta nel cestino).
- Vista "Pulizia": duplicati e versioni vecchie con spazio recuperabile, selezione multipla → cestino (mai cancellazione definitiva).

## 6. Piano di sviluppo

| Fase | Contenuto | Stima |
|---|---|---|
| 0. Setup ✅ | Repo, soluzione, CI GitHub Actions (build + test su `windows-latest`), convenzioni | 1–2 gg |
| 1. MVP indice ✅ | Scelta cartelle, scansione, categorie da estensione, DB SQLite+FTS5, ricerca istantanea, apri file/cartella | 1–2 sett. |
| 2. Metadati ✅ | Versione/produttore PE e MSI, firma Authenticode, architettura, raggruppamento per prodotto | 1–2 sett. |
| 3. Duplicati e pulizia | Hash a tre stadi, vista duplicati, versioni obsolete, spostamento nel cestino | 1 sett. |
| 4. Driver e installato | Parsing INF, incrocio con periferiche e con app installate | 1–2 sett. |
| 5. Monitoraggio | FileSystemWatcher, aggiornamento incrementale, tray icon, avvio con Windows | 1 sett. |
| 6. Organizzazione fisica | Spostamento scelto dall'utente con anteprima, log e annullamento ✅ (base). Da fare: regole salvate ("i driver NVIDIA vanno sempre in…") | 1 sett. |
| 7. Extra | Contenuto archivi/ISO, MFT/USN per scansione rapida, controllo aggiornamenti via winget, esportazione CSV | a piacere |

Stime per una persona a tempo parziale con assistenza AI; l'MVP utile (fasi 0–3) è realistico in circa 4–6 settimane.

## 7. Decisioni prese (9/11/2026)
1. **Organizzazione fisica sì, ma sempre decisa dall'utente**: l'app propone, l'utente sceglie per ogni file/gruppo *se* spostarlo e *dove* (cartella di destinazione scelta a mano o da regole salvate). Anteprima obbligatoria, log delle operazioni e annullamento. La fase 6 diventa parte del percorso principale.
2. **Interfaccia WPF** (.NET 10, tema Fluent, MVVM).
3. **Eseguibile portabile singolo** (`dotnet publish` self-contained, single-file, win-x64): niente installazione; dati in `%LOCALAPPDATA%\pcOrg`, oppure nella cartella `pcOrg-dati` accanto all'eseguibile (modalità portatile).
4. **Solo italiano**.
