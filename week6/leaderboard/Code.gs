/**
 * RacingBot Cup — 리더보드 랭킹 스크립트 (Google Apps Script)
 *
 * 채점은 전부 참가자 로컬 Unity에서 끝납니다. 이 스크립트가 하는 일은 제출된 결과를 펼쳐서
 * 순위를 매기는 것뿐이며, 점수를 다시 계산하지 않습니다.
 *
 * 설치:
 *   1. 제출용 Google Form을 만들고 응답을 스프레드시트에 연결합니다.
 *   2. 스프레드시트에서 확장 프로그램 > Apps Script를 열고 이 파일 내용을 붙여 넣습니다.
 *   3. RESPONSE_SHEET / CODE_COLUMN 을 실제 폼에 맞게 고칩니다.
 *   4. 트리거: onFormSubmit(폼 제출 시) 또는 시트 메뉴의 "리더보드 다시 만들기"를 씁니다.
 *
 * 자세한 절차는 같은 폴더의 README.md를 보세요.
 */

// ---------------------------------------------------------------------------
// 설정 — 실제 폼에 맞게 조정하세요.
// ---------------------------------------------------------------------------

/** 폼 응답이 쌓이는 시트 이름. */
var RESPONSE_SHEET = '설문지 응답 시트1';

/** 생성될 리더보드 시트 이름. */
var LEADERBOARD_SHEET = 'Leaderboard';

/** 제출 코드가 들어 있는 열의 헤더 텍스트 (부분 일치). */
var CODE_COLUMN_HEADER = '제출 코드';

/**
 * 체크섬 salt. Unity 쪽 SubmissionCodec.k_ChecksumSalt 와 반드시 같아야 합니다.
 * 이건 보안 장치가 아니라 오탈자·수기 편집을 잡는 용도입니다.
 */
var CHECKSUM_SALT = 'RacingBotCup/2026/score-v2';

var CODE_PREFIX = 'RBC1:';

/** 체크섬용 고정소수점 배율. Unity 쪽 SubmissionCodec.k_QuantizeScale 와 반드시 같아야 합니다. */
var QUANTIZE_SCALE = 1e6;

// ---------------------------------------------------------------------------
// 메뉴 / 트리거
// ---------------------------------------------------------------------------

function onOpen() {
  SpreadsheetApp.getUi()
    .createMenu('RacingBot Cup')
    .addItem('리더보드 다시 만들기', 'rebuildLeaderboard')
    .addToUi();
}

function onFormSubmit() {
  rebuildLeaderboard();
}

// ---------------------------------------------------------------------------
// 본체
// ---------------------------------------------------------------------------

function rebuildLeaderboard() {
  var spreadsheet = SpreadsheetApp.getActiveSpreadsheet();
  var responses = spreadsheet.getSheetByName(RESPONSE_SHEET);
  if (!responses) {
    throw new Error('응답 시트를 찾을 수 없습니다: ' + RESPONSE_SHEET);
  }

  var rows = responses.getDataRange().getValues();
  if (rows.length < 2) {
    return;
  }

  var codeColumn = findCodeColumn(rows[0]);
  if (codeColumn < 0) {
    throw new Error("'" + CODE_COLUMN_HEADER + "' 열을 찾을 수 없습니다.");
  }

  var best = {};

  for (var i = 1; i < rows.length; i++) {
    var code = String(rows[i][codeColumn] || '').trim();
    if (!code) {
      continue;
    }

    var entry = parseEntry(code, rows[i][0]);
    if (!entry) {
      continue;
    }

    var current = best[entry.participant];
    // 한 참가자당 가장 좋은 제출만 남깁니다. 여러 번 제출해도 손해는 없습니다.
    if (!current || compareEntries(entry, current) < 0) {
      best[entry.participant] = entry;
    }
  }

  var ranked = Object.keys(best).map(function (key) { return best[key]; });
  ranked.sort(compareEntries);

  writeLeaderboard(spreadsheet, ranked);
}

function parseEntry(code, submittedAt) {
  var payload;
  try {
    payload = decodeSubmission(code);
  } catch (error) {
    Logger.log('디코드 실패: ' + error);
    return null;
  }

  if (!payload || !payload.Score) {
    return null;
  }

  var verification = verifyChecksum(payload);

  return {
    participant: payload.ParticipantId || '(이름 없음)',
    total: numberOr(payload.Score.Total, 0),
    completion: numberOr(payload.Score.CompletionRate, 0),
    stdDev: numberOr(payload.Score.ScoreStdDev, 0),
    trackCount: numberOr(payload.Score.TrackCount, 0),
    seedSet: payload.SeedSetId || '',
    note: payload.Note || '',
    submittedAt: payload.SubmittedAtUtc || String(submittedAt || ''),
    integrity: payload.Integrity || {},
    checksumOk: verification.ok,
    checksumMessage: verification.message
  };
}

/** RBC1:<base64(gzip(json))> 를 되돌립니다. */
function decodeSubmission(code) {
  var trimmed = String(code).trim();
  if (trimmed.indexOf(CODE_PREFIX) !== 0) {
    throw new Error("코드가 '" + CODE_PREFIX + "' 로 시작하지 않습니다.");
  }

  var bytes = Utilities.base64Decode(trimmed.substring(CODE_PREFIX.length));
  var blob = Utilities.newBlob(bytes, 'application/x-gzip', 'payload.gz');
  return JSON.parse(Utilities.ungzip(blob).getDataAsString('UTF-8'));
}

/**
 * Unity의 SubmissionCodec.CanonicalForm 을 그대로 재현합니다.
 * 실수는 소수점 문자열이 아니라 정수로 들어갑니다 — 이유는 quantize() 주석을 보세요.
 */
function canonicalForm(payload) {
  var parts = [];
  parts.push(payload.SchemaVersion || '');
  parts.push('|');
  parts.push(payload.ParticipantId || '');
  parts.push('|');
  parts.push(payload.SeedSetId || '');
  parts.push('|');
  parts.push(payload.SubmittedAtUtc || '');
  parts.push('|');

  var score = payload.Score;
  if (score) {
    parts.push(quantize(score.Total));
    parts.push('|');
    parts.push(quantize(score.CompletionRate));
    parts.push('|');
    parts.push(quantize(score.ScoreStdDev));
    parts.push('|');
    parts.push(String(score.TrackCount));
    parts.push('|');

    var tracks = score.Tracks || [];
    for (var i = 0; i < tracks.length; i++) {
      var track = tracks[i];
      parts.push(String(track.Seed));
      parts.push(',');
      parts.push(quantize(track.BaselineTime));
      parts.push(',');
      parts.push(quantize(track.AgentTime));
      parts.push(',');
      parts.push(String(track.AgentStatus));
      parts.push(',');
      parts.push(String(track.BaselineStatus));
      parts.push(',');
      parts.push(quantize(track.Score));
      parts.push(';');
    }
  }

  var integrity = payload.Integrity;
  if (integrity) {
    parts.push('|');
    parts.push([
      integrity.CarSpecHash || '',
      integrity.TrackGeneratorVersion || '',
      integrity.ScoreModuleVersion || '',
      integrity.BaselineBotVersion || '',
      integrity.RulesHash || '',
      integrity.AgentHash || ''
    ].join(','));
  }

  parts.push('|');
  parts.push(CHECKSUM_SALT);
  return parts.join('');
}

function verifyChecksum(payload) {
  var claimed = payload.Integrity && payload.Integrity.Checksum;
  if (!claimed) {
    return { ok: false, message: '체크섬 없음' };
  }

  var actual = sha256Hex(canonicalForm(payload), 12);
  if (actual !== claimed) {
    return { ok: false, message: '체크섬 불일치' };
  }

  return { ok: true, message: '' };
}

function sha256Hex(text, byteCount) {
  var raw = Utilities.computeDigest(
    Utilities.DigestAlgorithm.SHA_256, text, Utilities.Charset.UTF_8);

  var hex = '';
  for (var i = 0; i < byteCount; i++) {
    var value = raw[i] < 0 ? raw[i] + 256 : raw[i];
    hex += ('0' + value.toString(16)).slice(-2);
  }

  return hex;
}

/** 기획서 §6 동점 처리: 총점 → 완주율 → 점수 표준편차. */
function compareEntries(a, b) {
  if (b.total !== a.total) {
    return b.total - a.total;
  }

  if (b.completion !== a.completion) {
    return b.completion - a.completion;
  }

  return a.stdDev - b.stdDev;
}

function writeLeaderboard(spreadsheet, ranked) {
  var sheet = spreadsheet.getSheetByName(LEADERBOARD_SHEET);
  if (!sheet) {
    sheet = spreadsheet.insertSheet(LEADERBOARD_SHEET);
  }

  sheet.clear();

  var header = [
    '순위', 'GitHub ID', '총점', '완주율', '점수 표준편차', '트랙 수',
    '시드 세트', '에이전트', '제출 시각(UTC)', '무결성', '설명'
  ];

  var table = [header];

  for (var i = 0; i < ranked.length; i++) {
    var entry = ranked[i];
    table.push([
      i + 1,
      entry.participant,
      round(entry.total, 4),
      round(entry.completion, 4),
      round(entry.stdDev, 4),
      entry.trackCount,
      entry.seedSet,
      entry.integrity.AgentHash || '',
      entry.submittedAt,
      entry.checksumOk ? 'OK' : ('⚠ ' + entry.checksumMessage),
      entry.note
    ]);
  }

  sheet.getRange(1, 1, table.length, header.length).setValues(table);
  sheet.getRange(1, 1, 1, header.length).setFontWeight('bold');
  sheet.setFrozenRows(1);
  sheet.autoResizeColumns(1, header.length);
}

// ---------------------------------------------------------------------------
// 유틸리티
// ---------------------------------------------------------------------------

function findCodeColumn(headerRow) {
  for (var i = 0; i < headerRow.length; i++) {
    if (String(headerRow[i]).indexOf(CODE_COLUMN_HEADER) >= 0) {
      return i;
    }
  }

  return -1;
}

/**
 * Unity의 SubmissionCodec.Quantize 와 같은 문자열을 만듭니다.
 *
 * 소수점 6자리 문자열(toFixed(6))로 맞추면 안 됩니다. Unity(Mono)의 ToString("F6")은 값의
 * 15자리 십진 표현을 반올림해서 28.0604515 → "28.060452" 를 주고, JS의 toFixed(6)은 실제
 * 2진값(28.06045149999999918…)을 반올림해서 "28.060451" 을 줍니다. float32 왕복 표기는
 * 이 6자리 경계에 자주 걸리기 때문에, 멀쩡한 제출이 계속 '체크섬 불일치'로 떨어졌습니다.
 *
 * 그래서 아예 소수점 포맷을 쓰지 않고 IEEE-754 곱셈·덧셈·floor 만으로 정수를 만듭니다.
 * 이 세 연산은 두 런타임이 비트 단위로 동일합니다.
 */
function quantize(value) {
  return String(Math.floor(numberOr(value, 0) * QUANTIZE_SCALE + 0.5));
}

function numberOr(value, fallback) {
  var number = Number(value);
  return isFinite(number) ? number : fallback;
}

function round(value, digits) {
  var factor = Math.pow(10, digits);
  return Math.round(value * factor) / factor;
}
