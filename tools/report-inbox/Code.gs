/**
 * Report inbox for Sandstorm Mod Launcher (Google Apps Script web app).
 *
 * The launcher sends a cleaned problem report here when a player presses "Send" (Settings > Something went wrong?).
 * Each report becomes a row in the "Reports" sheet of the spreadsheet this script belongs to, and the full text is
 * saved as a .txt file in a Drive folder called "Sandstorm Mod Launcher reports". Setup: see README.md next to this file.
 */
var FOLDER_NAME = 'Sandstorm Mod Launcher reports';
var MAX_BYTES = 250000;

function doPost(e) {
  try {
    var raw = e && e.postData ? e.postData.contents : '';
    if (!raw || raw.length > MAX_BYTES) return reply({ ok: false, error: 'size' });
    var d = JSON.parse(raw);
    if (!d || d.app !== 'SandstormModLauncher') return reply({ ok: false, error: 'app' });

    // A simple brake against floods: at most 30 reports an hour.
    var cache = CacheService.getScriptCache();
    var hour = 'n' + Math.floor(Date.now() / 3600000);
    var count = Number(cache.get(hour) || 0);
    if (count >= 30) return reply({ ok: false, error: 'busy' });
    cache.put(hour, String(count + 1), 3700);

    var id = Utilities.getUuid().substring(0, 8);
    var stamp = Utilities.formatDate(new Date(), 'UTC', 'yyyy-MM-dd_HH-mm-ss');
    var folders = DriveApp.getFoldersByName(FOLDER_NAME);
    var folder = folders.hasNext() ? folders.next() : DriveApp.createFolder(FOLDER_NAME);
    var file = folder.createFile('report-' + stamp + '-' + id + '.txt', String(d.full || ''), MimeType.PLAIN_TEXT);

    var book = SpreadsheetApp.getActiveSpreadsheet();
    var sheet = book.getSheetByName('Reports') || book.insertSheet('Reports');
    if (sheet.getLastRow() === 0) sheet.appendRow(['Received (UTC)', 'Id', 'Launcher', 'What happened', 'Summary', 'Full report']);
    sheet.appendRow([stamp.replace('_', ' '), id, String(d.version || ''), clip(d.note, 2000), clip(d.summary, 45000), file.getUrl()]);
    return reply({ ok: true, id: id });
  } catch (err) {
    return reply({ ok: false, error: String(err) });
  }
}

function doGet() {
  return reply({ ok: true, inbox: 'Sandstorm Mod Launcher reports' });
}

function clip(s, n) {
  s = String(s || '');
  return s.length > n ? s.substring(0, n) + '...' : s;
}

function reply(o) {
  return ContentService.createTextOutput(JSON.stringify(o)).setMimeType(ContentService.MimeType.JSON);
}
