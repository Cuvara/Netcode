# S13 predictor traces (live client), used by `LiveTraceReplayTests`

These are recorded by the TestLab predictor trace (`PredictorTraceSink`, Netcode 0.48.0 candidate) in
rpg-factory testlab scenario S13. In S13, client 0 blocks its main thread for 500 ms at scenario 10 s
and for 2 s at scenario 25 s.

| Fixture | Run | Source file sha256 (first 16 hex) | Lines kept / total |
|---|---|---|---|
| `2v6e.trace.ndjson.gz` | 20261023T054220Z-S13-2v6e | a5bb587e98ded6db | 40825 / 53353 |
| `me2y.trace.ndjson.gz` | 20261023T054339Z-S13-me2y | 78a4bb28acb02047 | 40722 / 53368 |
| `wqel.trace.ndjson.gz` | 20261023T054440Z-S13-wqel | 90771fd1af894328 | 42796 / 56500 |

- **Source.** `C:\Workspaces\UnityIndie\.testlab-runs\runs\<run>\client-0\trace.ndjson`, recorded by player
  `verify-netcode-8986908` / client d6c8839 against server 0b7a51a. The predictor code is that of Netcode
  d5ffb75; 8986908 only added the trace.
- **Trimming.** Each file runs from the start of the session to 3 s after the 2 s stall. It is cut at the
  first frame marker with `t` > (the 2 s stall frame's `t` + 3). It is gzip level 9 with mtime 0.
- **Format.** One JSON object per line:
  - frame markers `{"frame":N,"t":..,"udt":..,"dt":..}`;
  - a `header` line (on line 2 in this build);
  - a `session` line;
  - predictor events as written by `PredictorTraceEvent.AppendJson`.
- **`dev_arena.json`** is a byte copy of rpg-mmo-server `backend/content/maps/dev_arena.json` at 3f87b56,
  identical to the client's `Assets/Resources/Maps/dev_arena.json`. It is the map the session ran on
  (header `"map":"dev_arena"`). The replay needs it: the predictor clamps against its walls and boxes.
