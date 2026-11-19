# Screenshot di riferimento

Qui stanno le schermate approvate. A ogni push la build su GitHub genera gli screenshot
(ramo `screenshots`) e li confronta con questi: se una schermata cambia più dello 0,5 % dei pixel
la build fallisce e l'artifact **confronto-screenshot** contiene il riepilogo e, per ogni schermata,
un'immagine con i pixel cambiati in rosso.

Quando un cambiamento dell'interfaccia è voluto, aggiorna i riferimenti con gli screenshot della build:

```powershell
pwsh tools/aggiorna-riferimenti.ps1   # copia qui gli screenshot dell'ultima build (ramo screenshots)
```

Le schermate che dipendono dal computer della build (dischi e periferiche: `27`, `28`, `34`) non vengono confrontate.
Se la cartella non contiene immagini il confronto viene saltato.
