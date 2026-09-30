// Every rendered icon asset. `out` is relative to the output root (the repository root by
// default) and matches the file's location in the repo. `src` is relative to
// TinyWall/Resources/img/svg/. Add or drop an asset here, then run render.mjs and validate.mjs.

const APP_ICO = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const TRAY_ICO = [16, 20, 24, 32, 40, 48];
const IMG = 'TinyWall/Resources/img/';

const buttons16 = [
  'accept', 'add', 'cancel', 'connections', 'copy', 'delete', 'executable', 'exit',
  'export', 'import', 'info', 'manage', 'modify', 'network_drive_small', 'open_folder',
  'process', 'remove', 'search', 'store', 'uninstall', 'web', 'window', 'windows_small',
];

export const jobs = [
  { out: IMG + 'firewall.ico', src: 'firewall.svg', kind: 'ico', sizes: APP_ICO },
  { out: 'MsiSetup/Sources/ProgramFiles/SecureWall/SecureWall.ico', src: 'firewall.svg', kind: 'ico', sizes: APP_ICO },
  ...['green', 'yellow', 'red', 'grey', 'unknown'].map((c) => ({
    out: IMG + `shield_${c}_small.ico`, src: `shield_${c}.svg`, kind: 'ico', sizes: TRAY_ICO,
  })),
  ...buttons16.map((n) => ({ out: IMG + `${n}.png`, src: `${n}.svg`, kind: 'png', sizes: [16] })),
  { out: IMG + 'lock_small.png', src: 'lock.svg', kind: 'png', sizes: [16] },
  { out: IMG + 'lock.png', src: 'lock.svg', kind: 'png', sizes: [96] },
  ...['green', 'blue', 'red'].map((c) => ({
    out: IMG + `${c}_banner.png`, src: `${c}_banner.svg`, kind: 'png', sizes: [[540, 48]],
  })),
  { out: 'MsiSetup/banner.bmp', src: 'installer_banner.svg', kind: 'bmp', sizes: [[493, 58]] },
  { out: 'MsiSetup/background.bmp', src: 'installer_dialog.svg', kind: 'bmp', sizes: [[493, 312]] },
];
