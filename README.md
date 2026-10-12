# Token Window

A small Windows desktop widget showing the Codex seven-day allowance window ending at its recorded renewal timestamp. Displays actual used/remaining percentages and a dashed remaining-allowance forecast. Auto mode defaults to recent six-hour usage with a week-history fallback; manual 6-hour, 24-hour, and whole-week modes are also available. Resize it or enable Always on top.

## Run

Build using the .NET 8 SDK or later:

```powershell
dotnet run --project TokenWidget
```

Create a standalone Windows x64 executable (no installed .NET runtime required):

```powershell
dotnet publish TokenWidget -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/widget
```

## Data and forecast

Reads only `event_msg/token_count` weekly `rate_limits` snapshots from the last eight days under `%USERPROFILE%\.codex\sessions` (or `%CODEX_HOME%\sessions`). Discovers recently modified logs throughout the session tree, including conversations created in older date folders. Accepts a 10,080-minute meter in either primary or secondary. Does not read credentials, contact a service, or modify Codex logs. Refreshes every 30 seconds using incremental shared-file reads. Percentages are recorded account allowance readings, not inferred from raw token counts. Only locally recorded history is available; absent history is not fabricated.

The graph covers exactly renewal minus seven days through renewal, displayed in local time. Actual observations form a stepped remaining-percentage series. Auto estimates percentage-point change divided by elapsed time using observations in the six hours before the current time. This follows the recent model mix through its measured allowance consumption. When that interval has no measurable increase or fewer than 15 minutes of history, Auto falls back to the available observations in the current week. It does not assume an unobserved zero reading at the start of the week. The status shows the chosen basis. Elapsed-time averaging includes idle periods and avoids weighting densely logged conversations more heavily.

For forecasting only, a downward meter excursion that recovers to its prior value within 30 minutes is excluded as a temporary inconsistent snapshot. Unrecovered or sustained downward adjustments start a new forecast segment. Recorded actual graph points remain unchanged. Manual modes use their selected interval ending at the latest reading. The forecast begins at the latest reading and is capped at zero or renewal. “Runs out” means estimated allowance exhaustion; renewal is a separate event. Old readings are labelled stale, and expired windows await new readings rather than silently manufacturing a fresh allowance. Forecasts assume the estimated pace continues; future model changes, idle time, and sparse/rounded observations can change them.

This is a standalone desktop window, not an extension of the Windows Widgets board. No launch-at-login or account switching is configured. Local session logs may contain readings from different accounts if the same Codex home is shared.
