# Learned timelines

Built by `tools/build_timeline.py` from Combat Recorder logs, for fights cactbot has no timeline
for. One `<territory>.txt` (cactbot format) and `<territory>.json` (tags, trigger ids, kills,
`trusted`) per zone. Mit Planner only loads zones marked `"trusted": true` (every boss recorded
dying at least twice) and never replaces a cactbot timeline. Files land here after review.
