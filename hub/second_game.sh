#!/bin/bash
# Starts a second copy of Caves of Qud next to the one already running, for testing QUDOnline with two players
# on one machine. The second copy writes its log to Player2.log so the first copy's Player.log is left alone.
# Both copies share the save folder and the options: load a different save in each.

APP="$HOME/Library/Application Support/Steam/steamapps/common/Caves of Qud/CoQ.app"
LOG="$HOME/Library/Logs/Freehold Games/CavesOfQud/Player2.log"

open -n "$APP" --args -logFile "$LOG"
echo "Second game started; its log is $LOG"
