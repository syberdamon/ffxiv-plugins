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

Mit Planner bundles fight timelines from cactbot, unmodified, under
`MitPlanner/Timelines/` (currently `ruby_weapon.txt`, `emerald_weapon.txt` and `diamond_weapon.txt`, from
`ui/raidboss/data/05-shb/trial/`, commit e79ea7f, 2026-10-08). Default mechanic
tags are taken from cactbot's matching trigger files. The timeline parser follows
cactbot's documented timeline format.

Source: https://github.com/OverlayPlugin/cactbot
License: Apache License 2.0. A full copy is included at
`MitPlanner/Timelines/CACTBOT_LICENSE.txt`.
