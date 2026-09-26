# Report inbox

Players can send a problem report from the launcher without any account (Settings > Something went wrong? > Send a report). They see the whole report first; it has no user or PC name, user folders, Steam IDs, account lines or screenshots. The launcher sends it to the address in [`report-endpoint.txt`](../../report-endpoint.txt) at the root of this repository. While that file has no address, the launcher opens the GitHub issue form instead.

## Set it up (about five minutes, free)

1. In Google Drive, create a new Google Sheet, for example "Sandstorm Mod Launcher reports".
2. In the sheet: Extensions > Apps Script. Replace the code with [`Code.gs`](Code.gs) and save.
3. Deploy > New deployment > type **Web app**. Execute as: **Me**. Who has access: **Anyone**. Deploy, and allow the permissions it asks for (it writes to this sheet and creates one Drive folder).
4. Copy the web app URL (it ends in `/exec`).
5. Put that URL on its own line in `report-endpoint.txt` in this repository and push it. Launchers pick it up the next time someone sends a report; no new release is needed.

Reports then appear as rows in the "Reports" sheet, with the full text as a file in the Drive folder "Sandstorm Mod Launcher reports". At most 30 reports an hour are accepted.

To stop receiving reports, remove the URL from `report-endpoint.txt` (or delete the deployment).
