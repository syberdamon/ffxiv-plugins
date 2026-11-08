# Third-party notices

## CBT / Automaton (Puni.sh)

Map Click Nav's teleport-then-travel flow and nearest-aetheryte lookup are adapted
from CBT (formerly Automaton): `Tasks/CommonTasks.cs` and `Utilities/Coords.cs`.
Source: https://github.com/deoxys3/CBT (mirror of the Puni.sh project).

```
BSD 3-Clause License

Copyright (c) 2023, Puni.sh

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

```

## cactbot (OverlayPlugin/cactbot)

Mit Planner bundles every fight timeline from cactbot's `ui/raidboss/data/`,
unmodified, in `MitPlanner/Timelines/cactbot.zip` (first bundled from commit
e79ea7f, 2026-10-08; refreshed weekly by `.github/scripts/sync_cactbot.py`).
Default mechanic tags in the bundle's `index.json` are derived from cactbot's
matching trigger files. The timeline parser follows cactbot's documented
timeline format. cactbot has no NOTICE file.

Source: https://github.com/OverlayPlugin/cactbot
License: Apache License 2.0. A full copy is included at
`MitPlanner/Timelines/CACTBOT_LICENSE.txt` and inside the bundle.

## References for Turn-In Manager (no code copied)
Turn-In Manager's knowledge of the game's Grand Company, materia-retrieval and context-menu windows
(callback values, data layouts, button nodes) was learned from reading these open-source projects.
Our code is our own; credit to their authors:
- Deliveroo (Liza Carvelli / VeraNala fork): expert delivery flow, tab and filter callbacks.
- XA-Slave (xa-io): where the Expert Delivery window keeps its full ordered item list, and checking it against the 40 rows the window exposes before trusting it.
- DailyRoutines, AutoExpertDelivery module: supply list data layout, hand-in callback, seal-cap logic.
- AutoRetainer (PunishXIV): Grand Company exchange data layout, purchase callback, rank/category tabs, venture dialog.
- Questionable (PunishXIV) and Pandora's Box (PunishXIV): exchange tab selection.
- ECommons (NightmareXIV): button-click technique, MateriaRetrieveDialog.
- Snowfall (DevYukine): materia retrieval through the item context menu.
- Lifestream (NightmareXIV): IPC used for travel (optional dependency).



## References for Material Stash (no code copied, except one signature)
- AutoRetainer (PunishXIV): its IPC (`OnSendRetainerToVenture`, `SetVenture`, `GetAdditionalRetainerData`,
  `PluginState.IsBusy`), the rules for which retainer can take which venture and how many items it brings back, and the
  game-function signature for "has this gathering item been gathered" (`48 89 5C 24 ?? 57 48 83 EC 20 8B D9 8B F9`).
- Allagan Tools (Critical-Impact): IPC used to seed retainer contents (optional dependency).
- Lifestream (NightmareXIV): `/li inn` through its `ExecuteCommand` IPC (optional dependency); vnavmesh for walking.
- Spring Cleaning (OhGodAGirl, MIT): how the glamour dresser's Glamour Creation list stores an item (agent event 14
  with the list index, then the confirmation's callback 0) and the Armoire's store call. No code copied.

## References for Saucer Run (no code copied)
- Saucy (PunishXIV): plays the Mini and Jumbo Cactpot; its NPC ids (Mini broker 1010445, Jumbo broker 1010446,
  cashier 1010451). Required at runtime for the Cactpots.
- DailyDuty (MidoriKami): how tickets are counted (window openings) and the Jumbo Cactpot drawing times by region.
- AutoDuty (ffxivcode): the Triple Triad Trader's id, position and selling callbacks.
- ECommons (NightmareXIV): the layout of the Triple Triad card exchange list.
- Teamcraft (MIT): NPC map positions.

## Teamcraft data (Retainer Gear, Material Stash)

Retainer Gear and Material Stash (clean-up sources) bundle dungeon-drop and shop-trade lists derived from Teamcraft's public data
(`libs/data/src/lib/json/instance-sources.json`, `instances.json`, `shops.json`).
Source: https://github.com/ffxiv-teamcraft/ffxiv-teamcraft

```
MIT License

Copyright (c) 2017 Flavien Normand

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
