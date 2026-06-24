Battlegrounds Opponent Tracker

A Hearthstone Deck Tracker (HDT) plugin that helps you keep track of which Battlegrounds opponents you've recently faced, so you know who you can't face in the upcoming turns.

Features

- Marks recently-faced opponents with a red cross overlay
- Tracks Player IDs (1, 2, 3) and highlights them
- Player ID 1 is generally assigned the "ghost" (when available)


Known limitations

- The plugin can sometimes struggle to tell players apart when they have the same health + armor
- Currently only works correctly at 1920x1080 resolution (other resolutions may be supported in a future update)
- Ghost-assignment logic is still being validated against local match data


Installation


- Download the .dll file from this repo (see Releases).
- Open Hearthstone Deck Tracker, go to Settings → Plugins → Plugin Folder, and place the .dll there.
- Restart HDT.
- Go to Settings → Plugins, find "Battlegrounds Opponent Tracker", and tick the checkbox to activate it.
- (Optional) Click the Tracker button to hide the debug window from the overlay.
- The plugin will automatically load once a Battlegrounds game starts.
