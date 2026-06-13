# Big Trade Trap

Indicatore per **Quantowerm** che identifica visivamente i **trap level** sul grafico, basandosi sull'analisi del volume (Volume Analysis).

![Big Trade Trap](img/bigtradertrap.png)

## Come funziona

L'indicatore analizza i **Price Levels** della Volume Analysis e calcola per ogni livello:

- **Buy %** — percentuale del volume in acquisto
- **Sell %** — percentuale del volume in vendita
- **Delta %** — differenza assoluta tra buy e sell normalizzata

Un livello viene marcato come **trap** quando:

- La percentuale buy **o** sell supera il **70%**
- Il **delta %** è maggiore del **60%** (forte squilibrio direzionale)
- Il trap level (`BuyVolume - SellVolume`) supera in valore assoluto la **soglia minima** configurabile

### Bull Trap (acquisti dominanti)
Il prezzo si è mosso al rialzo con volumi alti, ma il delta mostra che gli acquisti dominano eccessivamente — possibile trappola per i buyer.

### Bear Trap (vendite dominanti)
Il prezzo si è mosso al ribasso con volumi alti, ma le vendite dominano eccessivamente — possibile trappola per i seller.

## Elementi grafici

- **Etichetta** sul prezzo del livello con il valore numerico del trap level
- **Rettangolo di sfondo** con colore configurabile (bull/bear) e alpha proporzionale al valore assoluto del trap
- **Testo bianco** con alpha proporzionale al trap level
- **Linea orizzontale** che parte dalla barra successiva e termina quando il prezzo ritorna al livello
- **Trap Day** — sommatoria di tutti i trap level del giorno corrente, mostrata in alto a destra

## Parametri

| Parametro | Default | Descrizione |
|-----------|---------|-------------|
| Min Trap Level | 20 | Valore minimo del trap level per mostrare un livello |
| Bull Color | DarkCyan | Colore dello sfondo e della linea per i bull trap |
| Bear Color | DarkRed | Colore dello sfondo e della linea per i bear trap |

## Requisiti

- Piattaforma `Quantower` https://www.quantower.com
- Dati con Volume Analysis disponibile

## Installazione

1. Copia il file `BigTraders.dll` nella cartella `C:\Quantower\Settings\Scripts\Indicators`
2. Aggiungi l'indicatore al grafico
