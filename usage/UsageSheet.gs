/**
 * @OnlyCurrentDoc
 * Limits this script to the one Sheet it is attached to, instead of all of your spreadsheets.
 */

// BDAT anonymous usage: receives one row per BDAT button click and adds it to this Sheet.
// Paste this into Extensions > Apps Script of the usage Google Sheet. See usage/SETUP.md.
//
// Only rows with the right KEY, a plain button name (so new BDAT buttons need no change here) and sane values are kept. Each PC id is limited to
// MAX_PER_ID_PER_DAY rows a day and the whole Sheet to MAX_PER_DAY, so nobody can flood it.
// This script can only add rows: nothing that reaches it can read, change or delete the Sheet.

const KEY = 'PASTE-YOUR-KEY-HERE'; // the same random key as line 2 of usage-key.txt
const MAX_PER_ID_PER_DAY = 200;
const MAX_PER_DAY = 2000;
const RESULTS = ['ok', 'error', 'crash'];
const HEADERS = ['Time', 'PC id', 'Button', 'Result', 'Seconds', 'BDAT', 'SolidWorks', 'Error'];

function doPost(e) {
  const p = (e && e.parameter) || {};
  // Each refusal says which check failed, so BDAT's usage.log shows why.
  if (String(p.key || '').trim() !== KEY.trim()) return reply('no: key');
  if (!/^[A-Za-z0-9 ]{1,40}$/.test(p.button || '')) return reply('no: button');
  if (RESULTS.indexOf(p.result) < 0) return reply('no: result');
  if (!/^[0-9a-f]{12}$/.test(p.id || '')) return reply('no: id');
  const ms = Number(p.ms);
  if (!(ms >= 0 && ms < 3600000)) return reply('no: ms');

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

// Daily e-mail of crashes and errors. Run setUpDailyReport once from the editor (select it, Run, allow access);
// after that dailyReport runs every morning and e-mails the script's owner only if something went wrong in the
// last 24 hours. Triggers use the saved code, so changes here need no new deployment.
const REPORT_HOUR = 7;

function setUpDailyReport() {
  ScriptApp.getProjectTriggers()
    .filter(function (t) { return t.getHandlerFunction() === 'dailyReport'; })
    .forEach(function (t) { ScriptApp.deleteTrigger(t); });
  ScriptApp.newTrigger('dailyReport').timeBased().everyDays(1).atHour(REPORT_HOUR).create();
}

function dailyReport() {
  const sheet = SpreadsheetApp.getActiveSpreadsheet().getSheets()[0];
  if (sheet.getLastRow() < 2) return;
  const since = new Date(Date.now() - 24 * 3600 * 1000);
  const rows = sheet.getRange(2, 1, sheet.getLastRow() - 1, HEADERS.length).getValues()
    .filter(function (r) { return r[0] instanceof Date && r[0] >= since && (r[3] === 'crash' || r[3] === 'error'); });
  if (rows.length === 0) return;

  // Same button, result and error counted once, most frequent first.
  const groups = {};
  rows.forEach(function (r) {
    const k = r[2] + '|' + r[3] + '|' + r[7];
    if (!groups[k]) groups[k] = { button: r[2], result: r[3], error: r[7], count: 0, pcs: {}, bdat: {} };
    groups[k].count++;
    groups[k].pcs[r[1]] = true;
    groups[k].bdat[r[5]] = true;
  });
  const lines = Object.keys(groups).map(function (k) { return groups[k]; })
    .sort(function (a, b) { return b.count - a.count; })
    .map(function (g) {
      return g.count + 'x ' + g.button + ' ' + g.result + ' on ' + Object.keys(g.pcs).length + ' PC(s), BDAT ' +
        Object.keys(g.bdat).join(', ') + '\n   ' + (g.error || '(no message)');
    });
  const crashes = rows.filter(function (r) { return r[3] === 'crash'; }).length;
  MailApp.sendEmail(
    Session.getEffectiveUser().getEmail(),
    'BDAT: ' + crashes + ' crash(es), ' + (rows.length - crashes) + ' error(s) in the last day',
    lines.join('\n\n') + '\n\n' + SpreadsheetApp.getActiveSpreadsheet().getUrl());
}
