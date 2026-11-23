# BrewSearch Reverse Engineering Wiki

File format documentation for the games BrewSearch takes apart, read from the game data
and the games' own code. Where [OmegaStudio](https://github.com/Team-BrewSearch/OmegaStudio)
has a plugin for a game, every layout is checked against it.

| Game | Platforms | Checked against |
| --- | --- | --- |
| Disgaea DS | Nintendo DS | Retail ROM and external specs (no plugin) |
| DreamWorks Super Star Kartz | PS3, Xbox 360, Wii, 3DS | DWSSK plugin |
| Polarbit Racing Engine: Crash Bandicoot Nitro Kart 2, Nitro Kart 3D, Krazy Kart Racing, Reckless Getaway, Raging Thunder 2 | iOS | Polarbit plugin |
| Suzumiya Haruhi no Chourantou / Gekitou | Windows | Harugeki plugin |
| Virtuos DS Racing Engine: Madagascar Kartz, DreamWorks Super Star Kartz, Hot Wheels: Battle Force 5, Pimp My Ride: Street Racing, Monster Jam: Path of Destruction | Nintendo DS | Virtuos plugin |

## Layout

```
index.html        site home: games, page structure, notation
script.js         site registry and page shell
style.css         site styles
<Game>/<game>.html   game home: conventions, file list, OmegaStudio support
<Game>/<area>/*.html format pages
```

A page file holds only its content. `script.js` adds the head, sidebar, title,
subtitle, tag badge and the generated `<ul data-list>` lists from one registry.

## Adding a Page

1. Create the file with the standard skeleton (use `../script.js` for a game home):

   ```html
   <!DOCTYPE html>
   <html lang="en">
   <head>
   <meta charset="UTF-8" />
   <script src="../../script.js"></script>
   </head>
   <body>
   <p>What the format holds and where the game uses it.</p>
   </body>
   </html>
   ```

2. Add one `{ file, label, tag, sub, blurb }` entry to the game's `pages` in `script.js`.

A new game is one object in `GAMES`.

## Style

- Open with one paragraph on what the format is, then layout tables of
  `Offset | Size | Type | Description`, then `In OmegaStudio`.
- Write `Unknown` for fields whose purpose is not known, and say when a figure is measured
  rather than read from code.
- Notes use `<div class="note"><strong>Note:</strong> ...</div>`.
- British spelling.
