// BDAT anonymous usage: receives one row per BDAT button click and adds it to this Sheet.
// Paste this into Extensions > Apps Script of the usage Google Sheet. See usage/SETUP.md.
//
// Only rows with the right KEY, a known button name and sane values are kept. Each PC id is limited to
// MAX_PER_ID_PER_DAY rows a day and the whole Sheet to MAX_PER_DAY, so nobody can flood it.
// This script can only add rows: nothing that reaches it can read, change or delete the Sheet.

const KEY = 'PASTE-YOUR-KEY-HERE'; // the same random key as line 2 of usage-key.txt
const MAX_PER_ID_PER_DAY = 200;
const MAX_PER_DAY = 2000;
const BUTTONS = [
  'BDAT loaded', 'Murder Part', 'Save MCM', 'Create Origin', 'Open from EBOM', 'Waterjet DXF', 'Name Cut List',
  'Check Out', 'Check In', 'Update BDAT', 'BDAT version',
];
const RESULTS = ['ok', 'error', 'crash'];
const HEADERS = ['Time', 'PC id', 'Button', 'Result', 'Seconds', 'BDAT', 'SolidWorks', 'Error'];

function doPost(e) {
  const p = (e && e.parameter) || {};
  if (p.key !== KEY) return reply('no');
  if (BUTTONS.indexOf(p.button) < 0 || RESULTS.indexOf(p.result) < 0) return reply('no');
  if (!/^[0-9a-f]{12}$/.test(p.id || '')) return reply('no');
  const ms = Number(p.ms);
  if (!(ms >= 0 && ms < 3600000)) return reply('no');

  const lock = LockService.getScriptLock();
  if (!lock.tryLock(10000)) return reply('busy');
  try {
    if (!underLimit('id:' + p.id, MAX_PER_ID_PER_DAY) || !underLimit('all', MAX_PER_DAY)) return reply('limit');
    const sheet = SpreadsheetApp.getActiveSpreadsheet().getSheets()[0];
    if (sheet.getLastRow() === 0) sheet.appendRow(HEADERS);
    sheet.appendRow([
      new Date(), p.id, p.button, p.result, Math.round(ms / 100) / 10,
      clean(p.bdat, 40), clean(p.sw, 40), clean(p.error, 300),
    ]);
  } finally {
    lock.releaseLock();
  }
  return reply('ok');
}

// Counts rows per day in the script cache. Returns false once count reaches max.
function underLimit(name, max) {
  const cache = CacheService.getScriptCache();
  const key = Utilities.formatDate(new Date(), 'UTC', 'yyyy-MM-dd') + ':' + name;
  const count = Number(cache.get(key) || 0);
  if (count >= max) return false;
  cache.put(key, String(count + 1), 21600); // 6 hours, the longest the cache keeps anything
  return true;
}

// Short plain text only. A leading = + - @ would make the Sheet treat it as a formula.
function clean(text, max) {
  let s = String(text || '').replace(/[\r\n\t]+/g, ' ').slice(0, max);
  if (/^[=+\-@]/.test(s)) s = "'" + s;
  return s;
}

function reply(text) {
  return ContentService.createTextOutput(text);
}
