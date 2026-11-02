// Facts the README and its art show, read from the source that owns each one.
// Every reader throws when its pattern stops matching, so a renamed button or a
// reworded status fails the build instead of leaving stale words in the art.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

const read = rel => fs.readFileSync(path.join(ROOT, rel), 'utf8');

function pick(rel, re, what) {
  const src = read(rel);
  const m = src.match(re);
  if (!m) throw new Error(`facts: ${what} not found in ${rel} (${re})`);
  // String captures are plain C# literals. An escape (\" or \n) or a "+"
  // concatenation would be cut short here, so refuse it instead of drawing half.
  if (/"$/.test(m[0]) && (m[1].includes('\\') || /^\s*\+/.test(src.slice(m.index + m[0].length)))) {
    throw new Error(`facts: ${what} in ${rel} uses an escape or concatenation; extend facts.mjs to read it`);
  }
  return m;
}

const POPUP = 'TinyWall/Prompting/BlockedConnectionPopup.cs';
const DESIGNER = 'TinyWall/Prompting/BlockedConnectionPopup.Designer.cs';
const STATUS = 'TinyWall/Prompting/NetworkActivityStatus.cs';
const RELEASE = 'tools/release/Build-SecureWallRelease.ps1';

const rgb = m => '#' + m.slice(1, 4).map(n => Number(n).toString(16).padStart(2, '0')).join('').toUpperCase();
const NAMED = { White: '#FFFFFF', Black: '#000000' };
const color = (control, prop) => {
  const m = pick(DESIGNER,
    new RegExp(`${control}\\.${prop} = Color\\.(?:FromArgb\\((\\d+), (\\d+), (\\d+)\\)|(White|Black)\\b)`),
    `${control}.${prop}`);
  return m[4] ? NAMED[m[4]] : rgb(m);
};
const text = control => pick(DESIGNER, new RegExp(`${control}\\.Text = "([^"]+)"`), `${control}.Text`)[1];
const rect = control => {
  const loc = pick(DESIGNER, new RegExp(`${control}\\.Location = new Point\\((\\d+), (\\d+)\\)`), `${control}.Location`);
  const size = pick(DESIGNER, new RegExp(`${control}\\.Size = new Size\\((\\d+), (\\d+)\\)`), `${control}.Size`);
  return { x: +loc[1], y: +loc[2], w: +size[1], h: +size[2] };
};

const client = pick(DESIGNER, /ClientSize = new Size\((\d+), (\d+)\)/, 'popup ClientSize');

export const FACTS = {
  popup: {
    w: +client[1],
    h: +client[2],
    title: pick(POPUP, /DefaultTitle = "([^"]+)"/, 'popup title')[1],
    titleBar: { ...rect('titlePanel'), fill: color('titlePanel', 'BackColor') },
    close: rect('closeButton'),
    identity: rect('identityLabel'),
    path: { ...rect('pathLabel'), ink: color('pathLabel', 'ForeColor') },
    destination: rect('destinationLabel'),
    destinationPrefix: pick(POPUP, /destinationLabel\.Text = \$"(Destination: )\{/, 'destination prefix')[1],
    notice: {
      ...rect('noticeLabel'),
      ink: color('noticeLabel', 'ForeColor'),
      text: pick(POPUP, /noticeLabel\.Text = prompt\.CanAllow\s*\?\s*"([^"]+)"/, 'allow notice')[1],
    },
    buttons: ['alwaysAllowButton', 'allowButton', 'ignoreButton'].map(id => ({
      id,
      label: text(id),
      fill: color(id, 'BackColor'),
      ink: color(id, 'ForeColor'),
      ...rect(id),
    })),
  },
  status: {
    allowed: pick(STATUS, /NetworkActivityStatus\.Allowed => "([^"]+)"/, 'Allowed status')[1],
    defaultDeny: pick(STATUS, /FilterGroup\.DefaultAction => "([^"]+)"/, 'default-deny status')[1],
  },
  platforms: pick(RELEASE, /\[string\[\]\]\$Platforms = @\(([^)]+)\)/, 'release platforms')[1]
    .split(',').map(s => s.trim().replace(/'/g, '')),
  upstream: {
    release: pick('UPSTREAM.md', /Upstream release: ([\d.]+)/, 'upstream release')[1],
    commit: pick('UPSTREAM.md', /Upstream commit \(pin\): `([0-9a-f]{40})`/, 'upstream commit')[1],
  },
};

const allow = FACTS.popup.buttons.find(b => b.id === 'allowButton');
if (!allow) throw new Error('facts: allowButton missing');
FACTS.popup.allow = allow;
