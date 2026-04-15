Put theme sound files in this folder.

Expected filenames for the sample theme.json:
- click.mp3
- select.mp3
- open.mp3
- close.mp3
- start.mp3
- music.mp3 (optional background music)

You can point to any relative file path in theme.json -> sounds.
For background music you can use either:
- "music": "sounds/music.mp3"
- or sounds.music inside the "sounds" object.

Custom background image for home screen:
- in theme.json use:
  "custom_background": { "enabled": true, "path": "background.jpg" }
