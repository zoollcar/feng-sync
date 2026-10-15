using System.Text;

namespace FengSync.Lan;

/// <summary>
/// The page a phone opens when it scans the pairing code, served at <c>GET /p</c>.
/// </summary>
/// <remarks>
/// <para>
/// A string constant in here rather than a file on disk, for two reasons this repository has already learned:
/// a loose file rides in the hash of every release manifest, and an embedded resource is a mechanism nothing
/// else in this codebase uses. Everything the page needs is already in <see cref="LanApi"/> and
/// <see cref="LanProtocol"/>, and it contacts no other origin — no framework, no build step, no request that
/// leaves the local network.
/// </para>
/// <para>
/// It is the same room the computer shows, from the only angle a browser can take: it reads this machine's copy
/// of the history by polling, adds a sentence to it, and pushes bytes at it. A browser has no listener, so it is
/// never the member anybody asks — which is why its 接收 goes through the route that receives on its behalf
/// rather than pretending it can reach a peer.
/// </para>
/// <para>
/// It uses <c>crypto.getRandomValues</c> and deliberately never <c>crypto.subtle</c>: the second exists only in
/// a secure context, and this link is plain HTTP to a private address, so a signature the page cannot compute
/// is not a design option. That is the same reason the protocol carries a bearer token and no request
/// signature.
/// </para>
/// <para>
/// The token goes into <c>sessionStorage</c> and nowhere else — never into the address, which a photograph of a
/// QR code already is, and never into <c>localStorage</c>, which would outlive the tab that was handed a
/// one-time code. What has to outlive the tab is the device's <i>name</i>, and that is not stored here either:
/// it is read back from the row this machine keeps for the device, which is the one copy a rename writes to.
/// A browser cannot attach a header to a download link, so the long-lived credential also arrives as an
/// HttpOnly cookie set at pairing; the page never reads that cookie, and no URL here ever carries a token.
/// </para>
/// </remarks>
public static class LanPhonePage
{
    /// <summary>The media type the route answers with; the charset is added by the framework from <see cref="Body"/>.</summary>
    public const string ContentType = "text/html";

    /// <summary>The page is written in Chinese and English and switches itself, so its bytes are plain UTF-8 with no preamble.</summary>
    public static readonly Encoding Body = new UTF8Encoding(false);

    /// <summary>
    /// The budget the page is held to. It is a convenience surface served to somebody else's device over a local
    /// network, so it carries no framework, no build step, and no request to anywhere but this machine — and with
    /// the wording cut back to what a phone screen can show, that is two language tables and one readable
    /// sentence for every refusal the wire answers with. The number guards against the page quietly becoming an
    /// application; it is measured, not invented.
    /// </summary>
    public const int MaximumBytes = 20 * 1024;

    /// <summary>
    /// The document, with its line endings settled. A checkout that stores CRLF would otherwise ship a page a
    /// few hundred bytes larger than the one this budget was measured on, and the served bytes would depend on
    /// whose machine built them.
    /// </summary>
    public static readonly string Html = Document.Replace("\r\n", "\n");

    private const string Document = """
<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>Feng Sync</title>
<style>
body{margin:0;padding:12px;background:#11141b;color:#e9ecf3;font:16px/1.5 system-ui,sans-serif;display:flex;flex-direction:column;min-height:100vh;box-sizing:border-box}
h1{font-size:16px;margin:0}
p{margin:7px 0}
small{color:#9aa3b8;font-size:13px}
section{border:1px solid #262b3a;border-radius:12px;padding:10px;margin:0 0 10px}
button{font:inherit;background:#3b82f6;color:#fff;border:0;border-radius:8px;padding:9px 12px}
.g{background:#1b2030;color:#e9ecf3;border:1px solid #2c3346}
input[type=text]{font:inherit;width:100%;box-sizing:border-box;background:#0c0f15;color:#e9ecf3;border:1px solid #2c3346;border-radius:8px;padding:11px}
#code{text-transform:uppercase}
ul{list-style:none;margin:0;padding:0}
#room{flex:1;display:flex;flex-direction:column;min-height:0}
#stream{flex:1;overflow:auto;margin:0 0 6px}
#stream li{margin:0 0 8px;max-width:86%}
.m{display:block;border:1px solid #2c3346;border-radius:12px;padding:8px 10px;background:#1b2030;word-break:break-all;text-align:left}
.me{margin-left:auto}
.me .m{background:#24406e;border-color:#2f5488}
.bar{height:4px;background:#262b3a;border-radius:2px;margin-top:5px;overflow:hidden}
.bar i{display:block;height:100%;width:0;background:#3b82f6}
a{color:#7fb0ff;text-decoration:none}
.row{border-top:1px solid #262b3a;padding:7px 0}
.hd{display:none}
</style>
</head>
<body>
<h1 id="h1">Feng Sync</h1>
<p><small id="who"></small></p>
<section id="pair">
<h2 id="ph"></h2>
<p><small id="hint"></small></p>
<p><input id="code" type="text" autocomplete="off" maxlength="12"></p>
<p><button id="go"></button></p>
<p><small id="err"></small></p>
</section>
<section id="room" class="hd">
<p><small id="meh"></small> <input id="my" type="text" maxlength="60"> <button id="rn" class="g"></button></p>
<p><label><input id="auto" type="checkbox"> <small id="al"></small></label> <small id="where"></small></p>
<p><small id="mb"></small></p>
<ul id="stream"></ul>
<p id="empty"><small></small></p>
<p><input id="msg" type="text" maxlength="2000"></p>
<p><button id="talk"></button> <button id="fp" class="g"></button> <button id="fd" class="g"></button></p>
<p><input id="f" type="file" multiple class="hd"> <input id="d" type="file" webkitdirectory multiple class="hd"></p>
<ul id="upl"></ul>
<p><small id="note"></small></p>
<script>
(function () {
"use strict";
var zh = (navigator.language || "").toLowerCase().indexOf("zh") === 0;
var L = zh ? {
title: "局域网快传", pair: "配对", hint: "电脑窗口上的六位码。扫码已经替你填好了。", go: "配对",
my: "在这台电脑上我叫", rename: "改名", needName: "先填一个名字。", renamed: "这台电脑以后这样列出你。",
auto: "让这台电脑替我收下群里说的文件", where: "收下的文件落在它的接收目录，位置在电脑窗口顶部改。",
spoke: "群里说过话的设备：", none: "还没有",
empty: "还没有人在这个群里说话。", talk: "发送", files: "发文件", folder: "发文件夹",
take: "接收", fetch: "先让这台电脑收下", got: "已经在这台电脑上了",
waiting: "等待", sent: "已发到群里", sentAs: "已发到群里，存为", failed: "没发出去", retry: "重试",
isdir: "文件夹", isfile: "文件", noFolder: "这个浏览器选不了文件夹，只能发文件。",
errNet: "联系不上这台电脑。窗口是不是关了？",
errExpired: "二维码里的配对码已经过期，请在电脑上重新生成。",
errWrong: "这个地址回答的电脑，和二维码里的不是同一台。",
errVersion: "这台电脑的协议比本页新，请从窗口重新扫码。",
errOther: "配对没有成功：",
pair_required: "还没有配对。", bad_code: "配对码不对。", code_expired: "配对码已经过期。",
rate_limited: "试的次数太多，等一分钟再试。", device_identity_changed: "设备身份变了，请重新配对。",
declined: "电脑上拒绝了这次接收。", path_unsafe: "名字里有这台电脑不能用的字符。",
name_reserved: "这个名字是这台电脑保留的。", incomplete_body: "传输断了，文件没收完。",
verification_failed: "收到的和它声明的不一致。", bad_request: "请求不完整。",
not_found: "这一项已经不在了，或者这台电脑问不到它的字节。", name_conflict: "那里已有同名文件，改名再发。",
path_too_long: "名字太长了。", message_too_long: "这句话太长了。"
} : {
title: "LAN quick transfer", pair: "Pairing", hint: "The six-character code from the computer. Scanning filled it in.", go: "Pair",
my: "This computer lists me as", rename: "Rename", needName: "Type a name first.", renamed: "That is how this computer will list you.",
auto: "Let this computer take the files the room mentions", where: "They land in its receive folder; set it at the top of the window.",
spoke: "Devices that have spoken here: ", none: "nobody yet",
empty: "Nobody has said anything in this room yet.", talk: "Send", files: "Send files", folder: "Send folder",
take: "Receive", fetch: "Have this computer take it first", got: "Already on this computer",
waiting: "Waiting", sent: "Said in the room", sentAs: "Said in the room, stored as", failed: "Not sent", retry: "Retry",
isdir: "folder", isfile: "file", noFolder: "This browser cannot pick a folder, only files.",
errNet: "Cannot reach this computer. Did its window close?",
errExpired: "The code in that QR has expired. Regenerate it on the computer.",
errWrong: "This address answers as a different computer than the QR named.",
errVersion: "This computer speaks a newer protocol. Scan again from the window.",
errOther: "Pairing failed: ",
pair_required: "Not paired yet.", bad_code: "Wrong code.", code_expired: "The code expired.",
rate_limited: "Too many tries. Wait a minute.", device_identity_changed: "This device's identity changed; pair again.",
declined: "Somebody at the computer refused this.", path_unsafe: "That name has characters it cannot use.",
name_reserved: "That name is reserved on that computer.", incomplete_body: "The transfer broke; the file is incomplete.",
verification_failed: "The bytes did not match their claim.", bad_request: "Incomplete request.",
not_found: "That item is gone, or this computer could not ask anybody for its bytes.", name_conflict: "Taken there; rename and resend.",
path_too_long: "The name is too long.", message_too_long: "That sentence is too long."
};
function t(k) { return L[k] || k }
function el(i) { return document.getElementById(i) }
function put(i, v) { el(i).textContent = v }
document.documentElement.lang = zh ? "zh" : "en";
put("h1", t("title")); put("ph", t("pair")); put("hint", t("hint")); put("go", t("go"));
put("meh", t("my")); put("rn", t("rename")); put("al", t("auto")); put("where", t("where"));
put("talk", t("talk")); put("fp", t("files")); put("fd", t("folder"));
el("empty").firstChild.textContent = t("empty");
var TK = "fs_token", DK = "fs_device";
var tok = sessionStorage.getItem(TK) || "";
var me = sessionStorage.getItem(DK) || "";
var mine = "";
var sid = "", since = 0, beat = null, seen = {}, members = {}, auto = false;
var H = {};
location.hash.replace(/^#/, "").split("&").forEach(function (kv) {
    var i = kv.indexOf("=");
    if (i > 0) H[kv.slice(0, i)] = decodeURIComponent(kv.slice(i + 1));
});
function api(p) { return "/api/v1" + p }
// A header value has to be ASCII, so the phone names itself in a form the computer can always read; the word a
// person sees for it is that computer's own translation of the kind, not something a page writes into it.
function deviceName() {
    var p = navigator.userAgentData && navigator.userAgentData.platform;
    return p && /^[ -~]+$/.test(String(p)) ? String(p) : "phone";
}
function newId() {
    var a = crypto.getRandomValues(new Uint8Array(32)), s = "";
    for (var i = 0; i < a.length; i++) s += (a[i] < 16 ? "0" : "") + a[i].toString(16);
    me = s; sessionStorage.setItem(DK, me); return me;
}
// The id of one sentence is minted here, before the request leaves, because the same sentence reaches every copy
// of this room by its own route. A page that let each machine number them would read its own words back twice.
function sayId() {
    var a = crypto.getRandomValues(new Uint8Array(16)), s = "";
    for (var i = 0; i < a.length; i++) s += (a[i] < 16 ? "0" : "") + a[i].toString(16);
    return s;
}
function auth() { return tok ? { "Authorization": "FS-LAN " + me + ":" + tok } : {} }
function peerHeaders() {
    var h = auth();
    h["X-FS-Device-Name"] = deviceName();
    h["X-FS-Device-Kind"] = "phone";
    return h;
}
function why(j) { var r = j && j.reason; return L[r] ? t(r) : t("errOther") + (r || "") }
function read(x) { try { return JSON.parse(x) } catch (e) { return null } }
function size(n) {
    if (n < 1024) return n + " B";
    var u = ["KB", "MB", "GB"], i = -1, x = n;
    do { x /= 1024; i++ } while (x >= 1024 && i < 2);
    return (x < 10 ? x.toFixed(1) : Math.round(x)) + " " + u[i];
}
function bad(m) { put("err", m || "") }
function note(m) { put("note", m || "") }
function get(p) {
    return fetch(api(p), { headers: auth() }).then(function (r) {
        return r.text().then(function (x) { return { s: r.status, j: read(x) } });
    });
}
function post(p, body) {
    var h = auth();
    if (body !== null) h["Content-Type"] = "application/json";
    return fetch(api(p), { method: "POST", headers: h, body: body === null ? null : JSON.stringify(body) })
        .then(function (r) { return r.text().then(function (x) { return { s: r.status, j: read(x) } }); });
}
// One row of the room, and the one action it can carry. heldHere is a statement about the computer this page is
// talking to, not about the room: on its own copy that machine either has these bytes or it does not, and only the
// second answer can become a link a browser is allowed to press.
function bubble(e) {
    if (!e || seen[e.messageId]) return;
    seen[e.messageId] = 1;
    if (e.authorDeviceId) members[e.authorDeviceId] = e.authorName;
    var li = document.createElement("li"), who = document.createElement("small"), m = document.createElement("span");
    if (e.authorDeviceId === me) li.className = "me";
    who.textContent = e.authorName || "";
    m.className = "m";
    if (e.kind === "text") {
        m.textContent = e.text || "";
    } else {
        var b = document.createElement("b"); b.textContent = e.fileName || "";
        var s = document.createElement("small");
        s.textContent = " " + size(e.bytes || 0) + " · " + t(e.isDirectory ? "isdir" : "isfile");
        m.appendChild(b); m.appendChild(s); m.appendChild(act(e));
    }
    li.appendChild(who); li.appendChild(m); el("stream").appendChild(li);
    el("empty").className = "hd";
    paint();
}
function act(e) {
    var w = document.createElement("span");
    if (e.heldHere) { w.appendChild(link(e)); return w }
    var btn = document.createElement("button");
    btn.className = "g"; btn.textContent = t("fetch");
    btn.onclick = function () { take(e, btn, w) };
    w.appendChild(btn);
    return w;
}
function link(e) {
    var a = document.createElement("a");
    // A folder is one archive here and cannot be resumed, which is the same promise the computer's own 接收 makes.
    a.href = api("/attachments/" + encodeURIComponent(e.messageId) + (e.isDirectory ? "/zip" : ""));
    a.download = e.isDirectory ? (e.fileName || "folder") + ".zip" : (e.fileName || "file");
    a.textContent = t("take");
    return a;
}
function take(e, btn, w) {
    btn.disabled = true;
    post("/attachments/" + encodeURIComponent(e.messageId) + "/receive", null).then(function (x) {
        if (x.s !== 200) { btn.disabled = false; note(why(x.j || {})); return }
        seen[e.messageId] = 2;
        w.textContent = ""; w.appendChild(link(x.j));
        note(t("got"));
    }, function () { btn.disabled = false; note(t("errNet")) });
}
function paint() {
    var names = [], k;
    for (k in members) { if (k !== me && members[k]) names.push(members[k]) }
    if (mine) names.unshift(mine);
    put("mb", t("spoke") + (names.length ? names.join("、") : t("none")));
}
function poll() {
    get("/log?since=" + since).then(function (x) {
        if (x.s !== 200) { stop(); bad(x.s === 403 || x.s === 401 ? t("pair_required") : why(x.j || {})); return }
        put("err", "");
        since = x.j.head || 0;
        (x.j.entries || []).forEach(function (e) {
            bubble(e);
            // An announced file this computer does not hold is worth fetching on its own: the browser cannot dial
            // anybody, so the only member that could hand these bytes over is the one already answering this poll.
            if (auto && e.kind !== "text" && !e.heldHere && e.authorDeviceId !== me && seen[e.messageId] === 1) {
                seen[e.messageId] = 3;
                post("/attachments/" + encodeURIComponent(e.messageId) + "/receive", null);
            }
        });
    }, function () { /* a beat that cannot reach the computer is a pause, not a loss: the next one asks again */ });
}
function stop() { if (beat) { clearInterval(beat); beat = null } }
function enter(row) {
    if (row && row.deviceId) { me = row.deviceId; sessionStorage.setItem(DK, me) }
    if (row && row.deviceName) { mine = row.deviceName; el("my").value = mine }
    el("pair").className = "hd"; el("room").className = "";
    since = 0; seen = {}; members = {};
    poll();
    if (!beat) beat = setInterval(poll, 3000);
}
function pair(code) {
    bad("");
    fetch(api("/pair"), {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code: code, deviceId: newId(), deviceName: deviceName(), kind: "phone" })
    }).then(function (r) {
        return r.text().then(function (x) { return { s: r.status, j: read(x) } });
    }).then(function (x) {
        if (x.s !== 201) { bad(x.j ? why(x.j) : t("errOther") + x.s); return }
        tok = x.j.token; sessionStorage.setItem(TK, tok);
        enter(x.j.peer);
    }, function () { bad(t("errNet")) });
}
function talk() {
    var text = el("msg").value;
    if (!text.trim()) return;
    var id = sayId();
    post("/messages", { text: text, messageId: id }).then(function (x) {
        if (x.s !== 201) { note(why(x.j || {})); return }
        el("msg").value = "";
        bubble(x.j);
    }, function () { note(t("errNet")) });
}
function uprow(name, status) {
    var li = document.createElement("li"), b = document.createElement("b"), s = document.createElement("small"), w = document.createElement("div");
    li.className = "row";
    b.textContent = name; w.className = "bar"; w.appendChild(document.createElement("i"));
    li.appendChild(b); li.appendChild(s); li.appendChild(w); el("upl").appendChild(li);
    return {
        bar: w.firstChild, li: li,
        set: function (v) { s.textContent = v },
        drop: function () { li.parentNode.removeChild(li) }
    };
}
function rows(files, isFolder) {
    var n = files.length, i = 0;
    sid = me.slice(0, 12) + "-" + Date.now().toString(36);
    // One file per request, and the next one starts only when the previous one has an answer, so a folder that
    // breaks halfway leaves exactly the files the phone already showed as sent. A whole tree is one sentence in
    // the room, so every write of it carries the same id and the same 文件夹 kind.
    var id = sayId();
    (function next() { if (i < n) send(files[i++], isFolder, i, n, next, id); })();
}
function send(file, isFolder, index, total, next, id) {
    var r = uprow(file.name, t("waiting") + " " + index + "/" + total);
    var rel = (isFolder && file.webkitRelativePath) ? file.webkitRelativePath : file.name;
    var x = new XMLHttpRequest();
    x.open("POST", api("/inbox"));
    var h = peerHeaders();
    h["X-FS-Rel"] = encodeURIComponent(rel);
    h["X-FS-Total"] = String(file.size);
    h["X-FS-Mtime"] = String(file.lastModified || 0);
    h["X-FS-Session"] = sid;
    h["X-FS-Msg"] = id;
    if (isFolder) h["X-FS-Kind"] = "directory";
    for (var k in h) x.setRequestHeader(k, h[k]);
    x.upload.onprogress = function (e) {
        if (e.lengthComputable) r.bar.style.width = Math.round(e.loaded * 100 / e.total) + "%";
    };
    function done(message, retry) {
        r.set(message);
        if (!retry) { next(); return }
        var btn = document.createElement("button");
        btn.className = "g"; btn.textContent = t("retry");
        btn.onclick = function () { r.drop(); send(file, isFolder, 1, 1, function () { }, id) };
        r.li.appendChild(btn);
        next();
    }
    x.onload = function () {
        if (x.status >= 200 && x.status < 300) {
            var j = read(x.responseText) || {};
            done(t(j.renamed ? "sentAs" : "sent") + " " + (j.savedAs || file.name), false);
            poll();
        } else {
            var j2 = read(x.responseText);
            done(t("failed") + "：" + (j2 ? why(j2) : x.status), true);
        }
    };
    x.onerror = function () { done(t("failed") + "：" + t("errNet"), true) };
    x.send(file);
}
el("go").onclick = function () { pair(el("code").value.toUpperCase()) };
el("talk").onclick = talk;
el("msg").onkeydown = function (e) { if (e.key === "Enter") { e.preventDefault(); talk() } };
el("rn").onclick = function () {
    var wanted = el("my").value;
    if (!wanted.trim()) { note(t("needName")); return }
    post("/name", { deviceName: wanted }).then(function (x) {
        if (x.s !== 200) { note(why(x.j || {})); return }
        // What is kept on screen is the name the computer stored, not the one that was typed, for the same reason
        // a received file is shown under the name the directory took.
        mine = x.j.deviceName || wanted; el("my").value = mine; note(t("renamed")); paint();
    }, function () { note(t("errNet")) });
};
el("auto").onchange = function () { auto = this.checked };
// A chosen file list is copied before the input is cleared, because emptying the input is the only way to let
// the same file be picked again and a browser may empty the live list out from under a transfer in progress.
el("fp").onclick = function () { el("f").click() };
el("fd").onclick = function () { el("d").click() };
el("f").onchange = function () { rows([].slice.call(this.files), false); this.value = "" };
el("d").onchange = function () {
    var files = [].slice.call(this.files);
    this.value = "";
    if (files.length && !files[0].webkitRelativePath) { note(t("noFolder")); return }
    rows(files, true);
};
fetch(api("/hello")).then(function (r) { return r.json() }).then(function (h) {
    put("who", (h.deviceName || "") + " · " + location.host);
    if (h.v !== 1) { bad(t("errVersion")); return }
    if (H.d && H.d !== h.deviceId) { bad(t("errWrong")); return }
    // The cookie is the only thing that can still be here when sessionStorage is not, so a tab reopened inside
    // the same browser session asks who it is rather than asking a person to pair again.
    get("/peers").then(function (x) {
        if (x.s === 200) { enter(x.j); return }
        if (tok) { enter(null); return }
        if (H.k) el("code").value = H.k;
        if (h.pairingArmed && H.k && (!H.e || (+H.e) * 1000 > Date.now())) pair(H.k);
        else if (H.k) bad(t("errExpired"));
    });
}, function () { bad(t("errNet")) });
})();
</script>
</body>
</html>

""";
}
