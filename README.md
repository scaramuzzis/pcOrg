# pcOrg

App per Windows che mette ordine tra installer, driver, programmi, archivi e immagini disco
sparsi nel PC. Scegli le cartelle da tenere sotto controllo; pcOrg le indicizza, divide i file
per categoria e li trova all'istante mentre scrivi. Sei tu a decidere se e dove spostarli: prima
vedi un'anteprima, e ogni spostamento si può annullare.

## Cosa fa oggi (v0.1)
- **Cartelle monitorate**: aggiungi/rimuovi cartelle; rimuoverne una non tocca i file su disco.
- **Categorie automatiche**: Installer, Driver, Programmi, Archivi, Immagini disco, Documenti,
  Immagini, Audio, Video, Script, Altro, con numero di file e spazio occupato.
- **Ricerca istantanea** per qualsiasi parte del nome o della cartella (`nvidia 566`, `setup x64`).
- **Azioni**: apri/esegui (con conferma per gli eseguibili), apri in Esplora risorse con il file
  selezionato, copia percorso.
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
1. Versione, produttore, firma digitale e architettura di exe/msi; raggruppamento per prodotto.
2. Cloni (hash) e versioni vecchie, con vista "Pulizia".
3. Driver: lettura dei file `.inf` e confronto con le periferiche presenti; incrocio con le app installate.
4. Aggiornamento automatico dell'indice quando cambiano le cartelle.
