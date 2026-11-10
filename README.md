# pcOrg

App per Windows che mette ordine tra installer, driver, programmi, archivi e immagini disco
sparsi nel PC. Scegli le cartelle da tenere sotto controllo; pcOrg le indicizza, divide i file
per categoria e li trova all'istante mentre scrivi. Sei tu a decidere se e dove spostarli: prima
vedi un'anteprima, e ogni spostamento si può annullare.

## Cosa fa oggi (v0.4)
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

Scorciatoie: `Ctrl+F` ricerca, `F5` aggiorna l'indice, doppio clic apre il file.

## Come si usa
Scarica `PcOrg.exe` (vedi sotto), copialo dove vuoi e avvialo: non serve installare nulla,
neanche .NET.

I dati (indice e cronologia) sono in `%LOCALAPPDATA%\pcOrg`. Per la **modalità portatile**
(es. su chiavetta) crea una cartella `pcOrg-dati` accanto a `PcOrg.exe`: i dati verranno salvati lì.

### Dove trovare l'eseguibile
- Nel repository, nella cartella **`dist/`**: `dist/PcOrg.exe` (versione 0.3.3).
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

Struttura, scelte tecniche e piano di sviluppo: [docs/ANALISI.md](docs/ANALISI.md).

## Prossimi passi
1. Cloni (hash) e vista "Pulizia" con spostamento nel Cestino.
2. Driver: lettura dei file `.inf` e confronto con le periferiche presenti; incrocio con le app installate.
3. Aggiornamento automatico dell'indice quando cambiano le cartelle.
