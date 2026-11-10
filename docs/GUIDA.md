# Guida di pcOrg

pcOrg mette ordine tra i file che si accumulano sul PC: installer, driver, programmi, documenti, foto, archivi e immagini disco. Analizza le cartelle che scegli, divide i file per categoria, legge versione, produttore e firma digitale di programmi e installer, e te li fa trovare all'istante. Sei sempre tu a decidere se e dove spostare i file: pcOrg non cancella e non sovrascrive nulla, e ogni spostamento si può annullare.

Questa guida spiega in dettaglio ogni funzione e come usarla al meglio. Puoi aprirla in qualsiasi momento con **F1** o dal menu **? → Guida di pcOrg**.

## Primo avvio

pcOrg è un unico file, **PcOrg.exe**, che non richiede installazione: copialo dove preferisci (per esempio in `C:\Programmi\pcOrg` o sul Desktop) e avvialo con un doppio clic.

**Avviso di Windows.** Al primo avvio Windows potrebbe mostrare "PC protetto da Windows", perché il programma non ha una firma digitale commerciale. Clicca **Ulteriori informazioni** e poi **Esegui comunque**: l'avviso non comparirà più.

**Schermata di benvenuto.** Finché non aggiungi una cartella vedi una schermata di benvenuto con il pulsante **Aggiungi la prima cartella**. Da lì in poi la finestra principale mostra le tue cartelle, le categorie e l'elenco dei file.

**Dove vengono salvati i dati.** L'elenco dei file (chiamato indice), la cronologia degli spostamenti e le preferenze sono salvati in `%LOCALAPPDATA%\pcOrg`. Per aprire questa cartella usa **Strumenti → Apri la cartella dei dati**.

> **Modalità portatile.** Per usare pcOrg da una chiavetta USB crea una cartella chiamata `pcOrg-dati` accanto a `PcOrg.exe`: i dati verranno salvati lì e viaggeranno con la chiavetta.

## La finestra principale

La finestra è divisa in quattro zone:

| Zona | A cosa serve |
|---|---|
| **Barra dei menu** | Tutti i comandi dell'applicazione, con le relative scorciatoie da tastiera. |
| **Intestazione** | Il logo, la barra di ricerca e i pulsanti **Aggiorna** e **Cronologia**. |
| **Pannello di sinistra** | Le **cartelle monitorate** e le **categorie** con il numero di file e lo spazio occupato. |
| **Area centrale** | Le schede **File** e **Prodotti e versioni**, i pulsanti di comando, l'elenco dei file, i dettagli del file selezionato e l'anteprima delle immagini. |

In basso la **barra di stato** indica cosa sta facendo pcOrg (per esempio "Scansione di C:\Download: 12.500 file…") e quanti file sono mostrati. Durante le operazioni lunghe compare una sottile barra di avanzamento sotto l'intestazione e il pulsante **Interrompi**.

Passando con il mouse su un pulsante compare un suggerimento che spiega cosa fa.

## Le cartelle monitorate

Le cartelle monitorate sono quelle che pcOrg tiene in ordine: tipicamente **Download**, **Desktop**, una cartella di installer o driver, un disco esterno o una cartella su un NAS.

**Aggiungere una cartella.** Usa il pulsante **+** nel pannello di sinistra, il menu **File → Aggiungi cartella** oppure **Ctrl+O**. pcOrg esamina subito la cartella e tutte le sottocartelle.

**Rimuovere una cartella.** Selezionala e usa il pulsante con il cestino o **File → Rimuovi la cartella selezionata**. I file **restano dove sono**: vengono solo tolti dall'elenco di pcOrg.

**Cartelle una dentro l'altra.** Se aggiungi una cartella che contiene cartelle già monitorate, queste vengono unite nella nuova. Non è invece possibile aggiungere una cartella che è già contenuta in un'altra monitorata: è già inclusa.

Sotto ogni cartella sono indicati il percorso completo e la data dell'ultimo aggiornamento.

## Cartelle di rete

pcOrg può tenere in ordine anche cartelle condivise su un **NAS** o su un **altro computer** della rete.

**Aggiungere una cartella di rete.** Usa il pulsante con l'icona di rete accanto al **+**, il menu **File → Aggiungi cartella di rete** oppure **Ctrl+Maiusc+O**. Scrivi il percorso nella forma `\\NOME-NAS\Condivisione\Cartella` (per esempio `\\NAS\Foto`) oppure sceglilo con **Sfoglia…**. Prima di aggiungerla pcOrg controlla che sia raggiungibile.

**Unità di rete (Z:, Y:…).** Se scegli una cartella su un'unità di rete collegata a una lettera, pcOrg la salva con il suo percorso di rete vero: così continua a funzionare anche se la lettera cambia o non è collegata all'avvio.

**Nome utente e password.** Se la condivisione richiede un accesso, aprila prima da Esplora risorse ed effettua l'accesso (spuntando "Memorizza credenziali"): pcOrg userà lo stesso accesso.

**Quando la cartella non è raggiungibile** (NAS spento, PC portatile fuori casa) compare in grigio con la scritta **Non raggiungibile**. I suoi file restano nell'elenco e si possono cercare con i dati dell'ultimo aggiornamento; vengono aggiornati appena la cartella torna disponibile. Se la rete cade durante una scansione, l'elenco di quella cartella resta com'era: non si perde nulla.

> Sui file in rete pcOrg legge chi ha firmato un programma ma non verifica la firma, perché la verifica richiederebbe di leggere ogni file per intero attraverso la rete. Per questo la firma compare con il simbolo **?**.

## Aggiornare l'indice

pcOrg non controlla le cartelle di continuo: aggiorna l'elenco quando glielo chiedi. Premi **F5**, il pulsante **Aggiorna** oppure **File → Aggiorna l'indice** dopo aver scaricato o spostato file con altri programmi.

L'aggiornamento avviene in due fasi:

1. **Scansione delle cartelle**: pcOrg trova i file nuovi, quelli modificati e quelli che non ci sono più. È molto veloce, anche con decine di migliaia di file.
2. **Lettura di versioni e firme**: per i programmi e gli installer nuovi o modificati (`.exe`, `.msi`, `.sys`) legge produttore, versione, architettura e firma digitale. Questa fase può richiedere qualche minuto la prima volta, perché la verifica della firma legge ogni file per intero; le volte successive vengono letti solo i file nuovi.

Puoi continuare a usare pcOrg durante l'aggiornamento. Il pulsante **Interrompi** nella barra di stato ferma l'operazione: riprenderà dal punto in cui si era fermata al prossimo aggiornamento.

## Le categorie

Ogni file viene assegnato a una categoria in base al tipo. Il pannello di sinistra mostra le categorie presenti, ciascuna con un'icona colorata, il numero di file e lo spazio occupato. Fai clic su una categoria per vedere solo quei file; **Tutti** mostra l'intero elenco.

| Categoria | Cosa contiene |
|---|---|
| **Installer** | Pacchetti `.msi`, `.msix`, `.appx` e programmi che si riconoscono come installazione (setup, installer, Inno Setup, NSIS…). |
| **Driver** | File `.inf`, `.sys`, `.cat` ed eseguibili o archivi di driver (nel nome, nella cartella o nella descrizione del file). |
| **Programmi** | Altri eseguibili `.exe`, per esempio programmi portatili. |
| **Archivi** | `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.cab` e simili. |
| **Immagini disco** | ISO, immagini Ghost, dischi virtuali, backup di sistema (vedi sotto). |
| **PDF** | Documenti `.pdf`. |
| **Word** | `.doc`, `.docx`, `.odt`, `.rtf`. |
| **Testo** | `.txt`, `.md`, `.log`, `.nfo`. |
| **Fogli di calcolo** | `.xls`, `.xlsx`, `.ods`, `.csv`. |
| **Presentazioni** | `.ppt`, `.pptx`, `.odp`. |
| **Altri documenti** | `.epub`, `.chm`, `.xps`, `.djvu`… |
| **Immagini, Audio, Video** | Foto e grafica, musica, filmati. |
| **Script** | `.bat`, `.cmd`, `.ps1`, `.reg`, `.py`… |
| **Altro** | Tutto ciò che non rientra nelle categorie precedenti. |

**Immagini disco suddivise per tipo.** Sotto "Immagini disco" compaiono, rientrate, le sottocategorie: **Ghost** (`.gho`, `.ghs`), **ISO**, **altri formati CD/DVD** (`.nrg`, `.mdf`, `.cue`…), **dischi virtuali** (`.vhd`, `.vhdx`, `.vmdk`, `.vdi`), **immagini Windows** (`.wim`, `.esd`), **backup Acronis** e **Macrium**, **immagini raw** (`.img`, `.dd`). I pezzi di un backup Ghost diviso in più file restano uno sotto l'altro, perché l'elenco è ordinato per nome.

**Categorie più precise grazie ai metadati.** Dopo la lettura di versioni e firme, un eseguibile che si descrive come driver passa in **Driver** e uno riconosciuto come installer passa in **Installer**, anche se il nome del file non lo diceva.

## Cercare i file

Scrivi nella barra di ricerca in alto (oppure premi **Ctrl+F**): i risultati compaiono mentre scrivi.

**Cosa viene cercato.** La ricerca trova il testo in qualsiasi punto del **nome del file**, del **nome della cartella**, del **nome del prodotto** e del **produttore**. Cercando `realtek` trovi quindi anche un file chiamato `setup.exe` se è un driver Realtek.

**Più parole.** Se scrivi più parole, vengono mostrati i file che le contengono tutte, in qualsiasi ordine: `nvidia 566` trova `566.36-desktop-win11-nvidia.exe`.

**Ricerca e categoria insieme.** La ricerca rispetta la categoria selezionata: scegli **PDF** e scrivi `fattura` per vedere solo le fatture in PDF.

**Cancellare la ricerca.** Premi **Esc** oppure il pulsante **✕** nella barra di ricerca.

Vengono mostrati al massimo 2.000 file alla volta: se la barra di stato lo segnala, aggiungi una parola o scegli una categoria per restringere la ricerca. L'esportazione in Excel include invece tutti i file trovati.

**Ordinare l'elenco.** Fai clic sull'intestazione di una colonna (Nome, Categoria, Versione, Dimensione, Modificato…) per ordinare; un secondo clic inverte l'ordine.

## Dettagli di un file

Selezionando un file, sotto l'elenco compare la scheda con tutti i suoi dettagli:

| Voce | Significato |
|---|---|
| **Prodotto** | Il nome del programma come dichiarato dal produttore (es. "Python", "Mozilla Firefox"). |
| **Descrizione** | La descrizione contenuta nel file. |
| **Produttore** | L'azienda che lo ha realizzato. |
| **Versione** | La versione del prodotto e, se diversa, quella del singolo file. |
| **Firma digitale** | Chi ha firmato il file e se la firma è valida (vedi sotto). |
| **Architettura** | 32 bit (x86), 64 bit (x64), ARM64, oppure "Qualsiasi (.NET)". |
| **Tipo di installer** | La tecnologia dell'installazione: Windows Installer (MSI), Inno Setup, NSIS, InstallShield, WiX, autoestraente 7-Zip/ZIP/RAR. |

**La firma digitale** dice chi ha prodotto davvero un programma ed è un ottimo indizio di affidabilità:

- **✔ Nome editore** — firma valida, verificata da Windows come fa Esplora risorse. Il file non è stato modificato dopo la firma.
- **✖ Non valida** — la firma c'è ma non è valida (file modificato o certificato non attendibile). Meglio non eseguire il file.
- **? Nome editore** — firma presente ma non verificata (per esempio sui file in rete).
- **Non firmato** — il file non ha firma: è normale per molti programmi gratuiti, ma conviene sapere da dove proviene.

Il pannello dettagli si può nascondere da **Visualizza → Pannello dettagli del file**.

## Anteprima delle immagini

Quando selezioni una foto o un'immagine, a destra dell'elenco compare il pannello **Anteprima** con l'immagine, le dimensioni in pixel e il peso del file. Con un doppio clic (o **Invio**) l'immagine si apre nel visualizzatore di Windows.

I formati supportati sono JPG, PNG, GIF, BMP, TIFF e ICO. Per **HEIC** (le foto dell'iPhone), **WebP** e **AVIF** serve l'estensione gratuita di Microsoft dallo Store: se manca, il pannello lo indica.

Il pannello si può nascondere da **Visualizza → Pannello anteprima immagini**.

## Prodotti e versioni

La scheda **Prodotti e versioni** (o **Ctrl+2**) raggruppa i programmi, gli installer e i driver per prodotto: le diverse versioni dello stesso programma scaricate nel tempo compaiono insieme.

Per ogni prodotto vedi il produttore, l'**ultima versione** presente, quante versioni e quanti file hai, lo spazio totale e lo spazio occupato dalle **versioni vecchie**. In alto un riepilogo indica quanto spazio potresti liberare.

Selezionando un prodotto, sotto compaiono i suoi file con lo stato:

- **Più recente** — l'ultima versione: di solito è quella da tenere.
- **Versione vecchia** — versioni superate, mostrate più chiare.

Il pulsante **Seleziona le versioni vecchie** le seleziona tutte in un clic: poi con **Sposta in…** puoi archiviarle in una cartella a parte. La casella **Solo prodotti con più file** mostra solo i prodotti di cui hai più copie o più versioni.

> Il raggruppamento usa il nome del prodotto e il produttore letti dal file. Per i file senza queste informazioni viene usato il nome del file, quindi qualche raggruppamento può essere meno preciso.

## Spostare i file

Seleziona uno o più file (con **Ctrl** o **Maiusc** per selezionarne più di uno, **Ctrl+A** per tutti) e usa **Sposta in…**, il menu **Modifica → Sposta in…** oppure **Ctrl+M**.

1. **Scegli la cartella di destinazione.**
2. **Controlla l'anteprima.** Una finestra elenca ogni file con la sua destinazione e lo stato:
   - ✔ **Da spostare** — il file verrà spostato con lo stesso nome;
   - ⓘ **Da spostare con nuovo nome** — nella destinazione esiste già un file con quel nome, quindi verrà chiamato per esempio `setup (2).exe`;
   - **Già nella destinazione** o **File non trovato** — verrà ignorato.
3. **Conferma** con il pulsante **Sposta N file**.

pcOrg **non sovrascrive mai** un file esistente e **non cancella nulla**. Se la cartella di destinazione non è monitorata, l'anteprima propone di aggiungerla, così i file spostati restano cercabili.

Al termine compare una notifica in basso a destra con il pulsante **Annulla**, che riporta subito i file dov'erano.

## Cronologia e annullamento

Ogni spostamento viene registrato. Apri **Strumenti → Cronologia spostamenti**, il pulsante **Cronologia** o **Ctrl+H** per vedere tutti gli spostamenti, con data, descrizione, numero di file e stato.

Seleziona uno spostamento per vedere l'elenco dei file coinvolti (da dove a dove) e usa **Riporta i file dov'erano** per annullarlo, anche a distanza di giorni.

L'annullamento non sovrascrive nulla: se nella posizione originale nel frattempo è comparso un altro file con lo stesso nome, quel file viene saltato e te lo segnala.

## Esportare in Excel

**File → Esporta l'elenco in Excel (CSV)** o **Ctrl+E** salva un file `.csv` con tutti i file della vista attuale, cioè rispettando la categoria selezionata e la ricerca. Il file contiene nome, categoria, versione, produttore, prodotto, firma, architettura, dimensione, data di modifica, cartella e percorso completo.

Il file è pensato per Excel in italiano: si apre con un doppio clic, con le colonne già separate e le lettere accentate corrette. Al termine la notifica offre il pulsante **Apri**.

Usi tipici: un inventario dei driver da conservare, l'elenco degli installer da masterizzare, il confronto tra due PC.

## Menu e scorciatoie da tastiera

| Azione | Scorciatoia |
|---|---|
| Cerca | **Ctrl+F** |
| Cancella la ricerca | **Esc** |
| Aggiungi cartella | **Ctrl+O** |
| Aggiungi cartella di rete | **Ctrl+Maiusc+O** |
| Aggiorna l'indice | **F5** |
| Esporta in Excel (CSV) | **Ctrl+E** |
| Apri il file | **Invio** o doppio clic |
| Mostra nella cartella | **Ctrl+Maiusc+E** |
| Copia percorso | **Ctrl+Maiusc+C** |
| Seleziona tutto | **Ctrl+A** |
| Sposta in… | **Ctrl+M** |
| Cronologia spostamenti | **Ctrl+H** |
| Scheda File / Prodotti e versioni | **Ctrl+1** / **Ctrl+2** |
| Guida | **F1** |

Anche i menu si usano da tastiera: premi **Alt** e la lettera sottolineata (per esempio **Alt+F** per il menu File). Con il tasto destro sull'elenco dei file compare un menu con le azioni più comuni.

**Apri** esegue il file con il programma predefinito. Per programmi, installer, driver e script pcOrg chiede prima conferma, per evitare di avviarli per sbaglio.

## Personalizzare pcOrg

Dal menu **Visualizza**:

- **Tema**: **Come Windows** (segue il tema chiaro o scuro del sistema), **Chiaro** oppure **Scuro**;
- **Pannello anteprima immagini** e **Pannello dettagli del file**: mostrali o nascondili per avere più spazio per l'elenco.

Le scelte vengono ricordate ai successivi avvii.

## Consigli per organizzare al meglio

1. **Parti dalle cartelle più disordinate**: Download, Desktop e i dischi esterni con installer e driver accumulati negli anni.
2. **Crea una cartella archivio** (per esempio `D:\Archivio`) con poche sottocartelle chiare: `Installer`, `Driver`, `Immagini disco`, `Documenti`. Aggiungila alle cartelle monitorate, così ritrovi tutto anche dopo lo spostamento.
3. **Usa le categorie come lista di lavoro**: seleziona **Driver**, ordina per produttore e sposta tutto in `Archivio\Driver`; ripeti con **Installer** e **Immagini disco**.
4. **Elimina i doppioni con "Prodotti e versioni"**: per ogni programma tieni l'ultima versione e sposta le vecchie in `Archivio\Versioni vecchie`. Se dopo qualche mese non ti sono servite, puoi cancellarle tu da Esplora risorse.
5. **Controlla la firma** prima di eseguire programmi scaricati tempo fa: preferisci quelli con firma valida ✔.
6. **Tieni insieme i pezzi dei backup Ghost** (`.gho` e i relativi `.ghs`): spostali sempre tutti insieme nella stessa cartella.
7. **Premi F5** dopo aver scaricato nuovi file o averli spostati con altri programmi.
8. **Esporta in Excel** un inventario dei driver dopo aver riordinato: è utile quando reinstalli Windows.

## Domande frequenti

**Non trovo un file che so di avere.** Controlla che la sua cartella sia tra quelle monitorate, premi **F5** per aggiornare l'indice e verifica che non sia selezionata una categoria diversa da **Tutti**.

**Un programma non ha versione o produttore.** Non tutti i file contengono queste informazioni: molti installer "scaricatori" ne sono privi. Per i file `.msi` le informazioni vengono lette con il servizio Windows Installer.

**Una cartella di rete è "Non raggiungibile".** Verifica che il NAS o il PC sia acceso e in rete e prova ad aprire la cartella da Esplora risorse; se richiede la password, inseriscila lì. Poi premi **F5**.

**La firma compare con "?".** Il file si trova in una cartella di rete: per non leggerlo per intero attraverso la rete la firma non viene verificata.

**L'anteprima di una foto non si vede.** Per HEIC, WebP e AVIF installa l'estensione gratuita di Microsoft dallo Store. Un file danneggiato non può essere mostrato.

**Ho spostato dei file per errore.** Usa **Annulla** nella notifica oppure **Strumenti → Cronologia spostamenti → Riporta i file dov'erano**.

**Voglio ricominciare da zero.** Chiudi pcOrg, apri la cartella dei dati (**Strumenti → Apri la cartella dei dati**) e cancella il file `indice.db`. Al successivo avvio ripartirai dalla schermata di benvenuto; i tuoi file non vengono toccati.

## Privacy e sicurezza

- pcOrg **non si collega a internet**: tutte le analisi avvengono sul tuo PC, compresa la verifica delle firme digitali.
- pcOrg **non cancella e non modifica** il contenuto dei file. Le uniche operazioni sui file sono gli spostamenti che confermi tu, sempre annullabili.
- Nessun file viene **sovrascritto**: in caso di nomi uguali viene aggiunto un numero, per esempio `setup (2).exe`.
- I dati di pcOrg (indice, cronologia, preferenze) restano nella cartella dei dati sul tuo PC.
