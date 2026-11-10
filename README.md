# pcOrg

App per Windows che mette ordine tra installer, driver, programmi, archivi e immagini disco
sparsi nel PC. Scegli le cartelle da tenere sotto controllo; pcOrg le indicizza, divide i file
per categoria e li trova all'istante mentre scrivi. Sei tu a decidere se e dove spostarli: prima
vedi un'anteprima, e ogni spostamento si può annullare.

## Cosa fa oggi (v0.9)
- **Cloni e pulizia** (Ctrl+3): trova i file con contenuto identico anche con nomi diversi
  (dimensione → impronta veloce → confronto completo, impronte memorizzate), mostra lo spazio
  recuperabile, segna i doppioni con una regola (tieni il più vecchio, il più recente…) e li sposta
  nel **Cestino di Windows** o in un'altra cartella. Non lascia mai un file senza nessuna copia.
- **Sposta nel Cestino** anche dalle schede File e Prodotti (tasto Canc).
- **Aggiornamento incrementale**: mentre l'app è aperta ogni file aggiunto, modificato o eliminato
  viene registrato subito (FileSystemWatcher); all'avvio e con F5 l'aggiornamento rapido rilegge solo
  le cartelle cambiate (100.000 file senza modifiche: ~10 ms). Scansione completa con Ctrl+F5.
- **Registro attività attivabile** dalle Impostazioni, con quattro livelli di dettaglio.
- **Categoria Accesso remoto**: file VNC (`.vnc`) e Desktop remoto (`.rdp`), con il computer di destinazione letto dal file.
- **Dati sempre salvati**: i file trovati vengono registrati man mano in `indice.db` e ricompaiono
  subito alla riapertura, anche dopo una scansione interrotta; chiusura sicura durante gli aggiornamenti;
  aggiornamento automatico all'avvio (disattivabile); **registro attività** in `log\pcorg.log`.
- **Impostazioni** (Strumenti → Impostazioni, `Ctrl+,`): tema, opzioni di aggiornamento e firme,
  **colonne visibili e loro ordine** per ogni elenco, informazioni e manutenzione dell'indice.
  Posizione, larghezza e ordinamento delle colonne, pannello laterale e finestra vengono ricordati.
- **Guida completa** dentro l'app (**F1** o menu **?**), con indice dei capitoli e ricerca. Lo stesso
  testo è leggibile qui: [docs/GUIDA.md](docs/GUIDA.md).
- **Barra dei menu** (File, Modifica, Visualizza, Strumenti, ?) con icone e scorciatoie da tastiera:
  aggiungi cartelle, **esporta l'elenco in Excel (CSV)**, scegli il **tema** (come Windows, chiaro,
  scuro), mostra o nascondi i pannelli anteprima e dettagli, guida rapida (F1) e Informazioni.
  Le preferenze vengono ricordate.
- **Interfaccia moderna** con tema Fluent di Windows 11 (chiaro/scuro automatico), icone colorate
  per ogni categoria, schermata di benvenuto, suggerimenti su ogni pulsante, finestre di conferma
  chiare e notifiche a comparsa (dopo uno spostamento la notifica offre subito **Annulla**).
- **Cartelle monitorate**: aggiungi/rimuovi cartelle; rimuoverne una non tocca i file su disco.
- **Cartelle di rete**: NAS e cartelle condivise di altri computer (`\\NAS\Foto`); le unità di rete
  mappate (es. `Z:`) vengono salvate con il percorso di rete. Se una cartella non è raggiungibile i suoi
  file restano cercabili (dall'ultimo aggiornamento) e vengono aggiornati quando torna disponibile;
  se la rete cade durante la scansione l'indice non perde nulla. Sui file in rete la firma digitale
  viene letta ma non verificata, per non scaricare ogni file per intero.
- **Categorie automatiche**: Installer, Driver, Programmi, Archivi, Immagini disco, PDF, Word
  (doc/docx/odt/rtf), Testo (txt/md/log), Fogli di calcolo, Presentazioni, Altri documenti, Immagini,
  Audio, Video, Script, Altro, con numero di file e spazio occupato.
- **Immagini disco suddivise per tipo**: Ghost (.gho/.ghs), ISO, altri formati CD/DVD (nrg, mdf,
  cue...), dischi virtuali (VHD, VMDK, VDI), immagini Windows (WIM, ESD), backup Acronis e Macrium,
  immagini raw (IMG, DD). Per exe e msi la
  categoria viene affinata leggendo il contenuto (es. un exe che si descrive come driver → Driver).
- **Versione, produttore, architettura e firma digitale** di `.exe`, `.msi` e `.sys`, letti dal
  file stesso. La firma è verificata come fa Windows (senza collegarsi a internet). Riconosce anche il
  tipo di installer: Inno Setup, NSIS, InstallShield, WiX, MSI, autoestraenti 7-Zip/ZIP/RAR.
- **Prodotti e versioni**: raggruppa le diverse versioni dello stesso programma o driver, indica la
  più recente e quanto spazio occupano le versioni vecchie; un clic le seleziona per spostarle.
- **Anteprima delle immagini** nel pannello a destra quando selezioni una foto o un'immagine
  (JPG, PNG, GIF, BMP, TIFF, ICO; WebP e HEIC con le estensioni gratuite di Microsoft), con dimensioni
  in pixel.
- **Ricerca istantanea** per nome, cartella, prodotto o produttore (`nvidia 566`, `setup x64`, `realtek`).
- **Azioni**: apri/esegui (con conferma per gli eseguibili), apri in Esplora risorse con il file
  selezionato, copia percorso; pannello con tutti i dettagli del file selezionato.
- **Sposta in…**: anteprima con stato di ogni file; non sovrascrive mai (in caso di nome già
  esistente usa `nome (2).ext`); propone di monitorare la cartella di destinazione.
- **Cronologia spostamenti** con annullamento.

Scorciatoie principali: `Ctrl+F` cerca, `Esc` cancella la ricerca, `F5` aggiorna, `Ctrl+O` aggiungi cartella,
`Ctrl+E` esporta in Excel, `Ctrl+M` sposta, `Ctrl+H` cronologia, `Invio` o doppio clic apre il file, `F1` guida.

## Come si usa
Scarica `PcOrg.exe` (vedi sotto), copialo dove vuoi e avvialo: non serve installare nulla,
neanche .NET.

I dati sono in `%LOCALAPPDATA%\pcOrg`: `indice.db` (cartelle, file, cronologia), `settings.json`
(preferenze) e `log\pcorg.log` (registro attività). Per la **modalità portatile**
(es. su chiavetta) crea una cartella `pcOrg-dati` accanto a `PcOrg.exe`: i dati verranno salvati lì.

### Dove trovare l'eseguibile
- Nel repository, nella cartella **`dist/`**: `dist/PcOrg.exe` (versione 0.6.1).
- Ogni push genera `PcOrg-win-x64` nella scheda **Actions** del repository (artifact della build).
- Le versioni pubblicate sono nella pagina **Releases** del repository (`PcOrg-win-x64.zip`).
  Una nuova Release si crea spingendo un tag `v*` oppure con un commit il cui messaggio
  contiene `[release]` (la versione è quella in `src/PcOrg.App/PcOrg.App.csproj`).

## Sviluppo
Requisiti: [.NET SDK 10](https://dotnet.microsoft.com/download).

```powershell
dotnet test                                   # test (girano anche su Linux/macOS)
dotnet run --project src/PcOrg.App            # avvia l'app (solo Windows)
dotnet publish src/PcOrg.App -c Release -o publish   # crea publish\PcOrg.exe
```

Documentazione funzionale (come si usa): [docs/GUIDA.md](docs/GUIDA.md).
Struttura, scelte tecniche e piano di sviluppo: [docs/ANALISI.md](docs/ANALISI.md).
Migliorie proposte: [docs/MIGLIORIE.md](docs/MIGLIORIE.md).

Sviluppato da **Scaramus**.

## Prossimi passi
1. Cloni (hash) e vista "Pulizia" con spostamento nel Cestino.
2. Driver: lettura dei file `.inf` e confronto con le periferiche presenti; incrocio con le app installate.
3. Aggiornamento automatico dell'indice quando cambiano le cartelle.
