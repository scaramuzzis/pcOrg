# pcOrg — Migliorie proposte

Proposte ordinate per **valore per l'utente / costo**. Ogni voce indica il problema, la soluzione e una stima indicativa (giorni di lavoro per una persona).

## Priorità alta

| # | Miglioria | Perché | Come | Stima |
|---|---|---|---|---|
| 1 | ✅ **Cloni esatti (hash)** e vista **Pulizia** (v0.9) | Oggi "Prodotti e versioni" trova versioni diverse, non le copie identiche (stesso file in due cartelle con nomi diversi). | Confronto a tre stadi: dimensione → hash dei primi/ultimi 64 KB → hash completo (xxHash3), solo sui candidati. Vista con gruppi di cloni, spazio recuperabile, "tieni il più recente". | 3–4 |
| 2 | ✅ **Sposta nel Cestino** (v0.9) | Per liberare spazio oggi bisogna passare da Esplora risorse. | `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`: mai eliminazione definitiva, sempre con conferma e registro. | 1 |
| 3 | ✅ **Aggiornamento automatico** delle cartelle (v0.8) | Bisogna premere F5 dopo ogni download. | `FileSystemWatcher` per le cartelle locali (con raggruppamento degli eventi ogni 2 s); per le cartelle di rete controllo periodico leggero. | 2–3 |
| 4 | **Regole di archiviazione** | Riordinare a mano Driver e Installer è ripetitivo. | Regole salvate ("Driver di NVIDIA → D:\Archivio\Driver\NVIDIA"), proposte nell'anteprima di spostamento; mai applicate senza conferma. | 3 |
| 5 | **Firma del codice di PcOrg.exe** | L'avviso "PC protetto da Windows" spaventa chi riceve l'app. | Certificato di firma del codice (OV o EV) e firma nella build di GitHub; in alternativa Microsoft Store/MSIX. | 1 + costo del certificato |

## Priorità media

| # | Miglioria | Perché | Come | Stima |
|---|---|---|---|---|
| 6 | **Driver vs periferiche presenti** | Capire quali driver servono ancora a questo PC. | Lettura dei file `.inf` (Class, Provider, DriverVer, HardwareID) e confronto con le periferiche via SetupAPI/`pnputil /enum-devices`. | 3–4 |
| 7 | **Installato / non installato** | Sapere se un installer è già stato usato. | Incrocio con le chiavi `Uninstall` del registro (HKLM, WOW6432Node, HKCU) e le app MSIX. | 2 |
| 8 | **Contenuto di ZIP/ISO** | Gli installer dentro gli archivi non vengono analizzati. | Indice delle voci di zip/7z/rar (SharpCompress) e delle ISO; ricerca anche dentro. | 3 |
| 9 | **Etichette e note** | Ricordare perché si tiene un file ("driver stampante ufficio"). | Tabella `tags` nell'indice, ricerca per etichetta, colonna facoltativa. | 2 |
| 10 | **Controllo aggiornamenti dei programmi** | Sapere se esiste una versione più nuova. | Confronto con i manifest pubblici di winget per i prodotti riconosciuti (opzionale, unica funzione con accesso a internet). | 3 |
| 11 | **Ricerca nel contenuto dei documenti** | Trovare un PDF o un Word da una parola del testo. | Estrazione testo (iFilter di Windows o librerie PDF/OpenXML) in una tabella FTS dedicata, solo per le cartelle scelte. | 4–5 |

## Qualità e manutenzione

| # | Miglioria | Perché | Come | Stima |
|---|---|---|---|---|
| 12 | **Aggiornamento automatico dell'app** | Oggi bisogna scaricare a mano ogni Release. | Controllo della Release più recente su GitHub e sostituzione dell'exe al riavvio (con conferma). | 2 |
| 13 | **Scansione veloce NTFS (MFT/USN)** | Su dischi interi con milioni di file l'enumerazione richiede minuti. | Lettura della Master File Table e del journal USN (richiede privilegi di amministratore): stesse prestazioni di "Everything". | 5 |
| 14 | **Test dell'interfaccia più ampi** | Gli screenshot automatici vanno guardati a occhio. | Confronto automatico con immagini di riferimento (tolleranza sui pixel) per accorgersi di regressioni grafiche. | 2 |
| 15 | **Separare il progetto Windows** | `PcOrg.Core` contiene alcune chiamate solo-Windows (MSI, WinVerifyTrust). | Spostarle in `PcOrg.Windows` dietro interfacce, per test più semplici e portabilità. | 1–2 |
| 16 | **Accessibilità** | Uso da tastiera e con screen reader. | Nomi di automazione su tutti i controlli, ordine di tabulazione, contrasto verificato nei due temi. | 1–2 |
| 17 | **Pacchetto MSIX / installer** | Alcune aziende preferiscono un installer gestito. | Pacchetto MSIX firmato in aggiunta all'exe portatile. | 1–2 |

## Note tecniche sullo stato attuale

- **Persistenza**: l'indice SQLite (WAL) salva i file a blocchi da 2.000 durante la scansione; le eliminazioni avvengono solo a scansione completa. Le preferenze sono scritte in modo atomico (file temporaneo + sostituzione).
- **Diagnostica**: `log\pcorg.log` con rotazione a 2 MB (3 file). Gli errori nelle operazioni in background vengono registrati e segnalati con una notifica invece di perdersi.
- **Limiti noti**: risultati mostrati fino a 2.000 per volta (l'esportazione CSV li include tutti); ordinamento per colonna applicato ai risultati caricati; la lettura dei file `.msi` è verificata solo nella build Windows.
