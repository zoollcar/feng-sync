import { size } from "../lib/format";

/**
 * The page's own two language tables.
 *
 * Written here rather than in the application's `.resx`, because this document is served to somebody else's device
 * over a local network and carries its own copy of every word it can show — the same reason the page has no
 * request that leaves this machine. The table is chosen from the browser, not from a setting: a phone that opens
 * this page once has no settings screen to visit.
 */
export type Dict = Record<string, string>;

const zh: Dict = {
  title: "局域网快传",
  room: "房间",
  pair: "配对",
  hint: "电脑窗口上的六位码。扫码或打开网址已经替你填好了。",
  go: "配对",
  my: "在这台电脑上我叫",
  rename: "改名",
  needName: "先填一个名字。",
  renamed: "这台电脑以后这样列出你。",
  auto: "让这台电脑替我收下群里说的文件",
  where: "收下的文件落在它的接收目录，位置在电脑窗口顶部改。",
  spoke: "群里说过话的设备：",
  none: "还没有",
  empty: "还没有人在这个群里说话。",
  talk: "发送",
  files: "发文件",
  folder: "发文件夹",
  take: "接收",
  fetch: "先让这台电脑收下",
  got: "已经在这台电脑上了",
  waiting: "等待",
  sent: "已发到群里",
  sentAs: "已发到群里，存为",
  failed: "没发出去",
  retry: "重试",
  isdir: "文件夹",
  isfile: "文件",
  noFolder: "这个浏览器选不了文件夹，只能发文件。",
  errNet: "联系不上这台电脑。窗口是不是关了？",
  errExpired: "二维码里的配对码已经过期，请在电脑上重新生成。",
  errWrong: "这个地址回答的电脑，和二维码里的不是同一台。",
  errVersion: "这台电脑的协议比本页新，请从窗口重新扫码。",
  errOther: "配对没有成功：",
  pair_required: "还没有配对。",
  bad_code: "配对码不对。",
  code_expired: "配对码已经过期。",
  rate_limited: "试的次数太多，等一分钟再试。",
  device_identity_changed: "设备身份变了，请重新配对。",
  declined: "电脑上拒绝了这次接收。",
  path_unsafe: "名字里有这台电脑不能用的字符。",
  name_reserved: "这个名字是这台电脑保留的。",
  incomplete_body: "传输断了，文件没收完。",
  verification_failed: "收到的和它声明的不一致。",
  bad_request: "请求不完整。",
  not_found: "这一项已经不在了，或者这台电脑问不到它的字节。",
  name_conflict: "那里已有同名文件，改名再发。",
  path_too_long: "名字太长了。",
  message_too_long: "这句话太长了。",
  transfers: "传输",
  receiving: "接收",
  sending: "发送",
  left: "剩 {0}",
  speedUnit: "/秒",
  done: "完成",
  stopped: "已中断",
  folderOf: "文件夹 · {0} 个文件",
  waitingApproval: "等待电脑确认 {0}/{1}",
  connecting: "正在连接这台电脑…",
  live: "进行中",
  devices: "设备",
  you: "你",
  collapse: "收起",
  expand: "展开",
  pickFiles: "选文件",
  pickFolder: "选文件夹",
};

const en: Dict = {
  title: "LAN quick transfer",
  room: "Room",
  pair: "Pairing",
  hint: "The six-character code from the computer. Scanning or opening the link filled it in.",
  go: "Pair",
  my: "This computer lists me as",
  rename: "Rename",
  needName: "Type a name first.",
  renamed: "That is how this computer will list you.",
  auto: "Let this computer take the files the room mentions",
  where: "They land in its receive folder; set it at the top of the window.",
  spoke: "Devices that have spoken here: ",
  none: "nobody yet",
  empty: "Nobody has said anything in this room yet.",
  talk: "Send",
  files: "Send files",
  folder: "Send folder",
  take: "Receive",
  fetch: "Have this computer take it first",
  got: "Already on this computer",
  waiting: "Waiting",
  sent: "Said in the room",
  sentAs: "Said in the room, stored as",
  failed: "Not sent",
  retry: "Retry",
  isdir: "folder",
  isfile: "file",
  noFolder: "This browser cannot pick a folder, only files.",
  errNet: "Cannot reach this computer. Did its window close?",
  errExpired: "The code in that QR has expired. Regenerate it on the computer.",
  errWrong: "This address answers as a different computer than the QR named.",
  errVersion: "This computer speaks a newer protocol. Scan again from the window.",
  errOther: "Pairing failed: ",
  pair_required: "Not paired yet.",
  bad_code: "Wrong code.",
  code_expired: "The code expired.",
  rate_limited: "Too many tries. Wait a minute.",
  device_identity_changed: "This device's identity changed; pair again.",
  declined: "Somebody at the computer refused this.",
  path_unsafe: "That name has characters it cannot use.",
  name_reserved: "That name is reserved on that computer.",
  incomplete_body: "The transfer broke; the file is incomplete.",
  verification_failed: "The bytes did not match their claim.",
  bad_request: "Incomplete request.",
  not_found: "That item is gone, or this computer could not ask anybody for its bytes.",
  name_conflict: "Taken there; rename and resend.",
  path_too_long: "The name is too long.",
  message_too_long: "That sentence is too long.",
  transfers: "Transfers",
  receiving: "Receiving",
  sending: "Sending",
  left: "{0} left",
  speedUnit: "/s",
  done: "Done",
  stopped: "Interrupted",
  folderOf: "Folder · {0} files",
  waitingApproval: "Waiting for the computer {0}/{1}",
  connecting: "Connecting to this computer…",
  live: "live",
  devices: "Devices",
  you: "you",
  collapse: "Collapse",
  expand: "Expand",
  pickFiles: "Pick files",
  pickFolder: "Pick a folder",
};

export const isChinese = (navigator.language || "").toLowerCase().indexOf("zh") === 0;
export const dict: Dict = isChinese ? zh : en;

/** One word, with `{0}`-style holes filled. An unknown key is shown as itself rather than as nothing. */
export function t(key: string, ...args: Array<string | number>): string {
  let text = dict[key] ?? key;
  args.forEach((value, index) => {
    text = text.split(`{${index}}`).join(String(value));
  });
  return text;
}

/** A rate in the unit this language writes it in. */
export function rateText(bytesPerSecond: number): string {
  return `${size(bytesPerSecond)}${t("speedUnit")}`;
}

/** A refusal, said in words. The reason codes are the protocol's, so a new one shows its own name rather than a lie. */
export function reasonText(reason: string): string {
  return reason && dict[reason] ? dict[reason] : t("errOther") + reason;
}
